// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Markup;

namespace Microsoft.UI.Xaml.Controls
{
	internal class ViewManager
	{
		private ItemsRepeater m_owner;

		// Pinned elements that are currently owned by layout are *NOT* in this pool.
		private List<PinnedElementInfo> m_pinnedPool;
		private UniqueIdElementPool m_resetPool;

		// _lastFocusedElement is listed in _pinnedPool.
		// It has to be an element we own (i.e. a direct child).
		UIElement m_lastFocusedElement;
		bool m_isDataSourceStableResetPending;

		// Event tokens
		//winrt::UIElement::GotFocus_revoker m_gotFocus { };
		//winrt::UIElement::LostFocus_revoker m_lostFocus { };

		Phaser m_phaser;

		// Cached generate/clear contexts to avoid cost of creation every time.
		ElementFactoryGetArgs m_ElementFactoryGetArgs;
		ElementFactoryRecycleArgs m_ElementFactoryRecycleArgs;

		// These are first/last indices requested by layout and not cleared yet.
		// These are also not truly first / last because they are a lower / upper bound on the known realized range.
		// For example, if we didn't have the optimization in ElementManager.cpp, m_lastRealizedElementIndexHeldByLayout 
		// will not be accurate. Rather, it will be an upper bound on what we think is the last realized index.
		int m_firstRealizedElementIndexHeldByLayout =  FirstRealizedElementIndexDefault;
		int m_lastRealizedElementIndexHeldByLayout = LastRealizedElementIndexDefault;
		const int FirstRealizedElementIndexDefault = int.MaxValue;
		const int LastRealizedElementIndexDefault = int.MinValue;

		public ViewManager(ItemsRepeater* owner)
		{
			// ItemsRepeater is not fully constructed yet. Don't interact with it.

			m_owner = owner;
			m_resetPool(owner);

			//m_lastFocusedElement(owner);
			//m_phaser(owner);
			//m_ElementFactoryGetArgs(owner);
			//m_ElementFactoryRecycleArgs(owner);
		}

		[Conditional("TRACE")]
		private static void REPEATER_TRACE_INFO(string text)
			=> Console.WriteLine(text);

		[Conditional("TRACE")]
		private static void REPEATER_TRACE_PERF(string text)
			=> Console.WriteLine(text);

		[Conditional("DEBUG")]
		private static void MUX_ASSERT(bool assertion)
			=> global::System.Diagnostics.Debug.Assert(assertion);

		public UIElement GetElement(int index, bool forceCreate, bool suppressAutoRecycle)
		{
			UIElement element = forceCreate ? null : GetElementIfAlreadyHeldByLayout(index);
			if (element == null)
			{
				// check if this is the anchor made through repeater in preparation 
				// for a bring into view.
				var madeAnchor = m_owner.MadeAnchor();
				if (madeAnchor != null)
				{
					var anchorVirtInfo = ItemsRepeater.TryGetVirtualizationInfo(madeAnchor);
					if (anchorVirtInfo.Index() == index)
					{
						element = madeAnchor;
					}
				}
			}

			if (element == null) { element = GetElementFromUniqueIdResetPool(index); }

			if (element == null) { element = GetElementFromPinnedElements(index); }

			if (element == null) { element = GetElementFromElementFactory(index); }

			var virtInfo = ItemsRepeater.TryGetVirtualizationInfo(element);
			if (suppressAutoRecycle)
			{
				virtInfo.AutoRecycleCandidate(false);
				REPEATER_TRACE_INFO("%* GetElement: %d Not AutoRecycleCandidate: \n", m_owner.Indent(), virtInfo.Index());
			}
			else
			{
				virtInfo.AutoRecycleCandidate(true);
				virtInfo.KeepAlive(true);
				REPEATER_TRACE_INFO("%* GetElement: %d AutoRecycleCandidate: \n", m_owner.Indent(), virtInfo.Index());
			}

			return element;
		}

		public void ClearElement(UIElement element, bool isClearedDueToCollectionChange)
		{
			var virtInfo = ItemsRepeater.GetVirtualizationInfo(element);
			int index = virtInfo.Index();
			bool cleared =
				ClearElementToUniqueIdResetPool(element, virtInfo) ||
				ClearElementToAnimator(element, virtInfo) ||
				ClearElementToPinnedPool(element, virtInfo, isClearedDueToCollectionChange);

			if (!cleared)
			{
				ClearElementToElementFactory(element);
			}

			// Both First and Last indices need to be valid or default.
			MUX_ASSERT((m_firstRealizedElementIndexHeldByLayout == FirstRealizedElementIndexDefault && m_lastRealizedElementIndexHeldByLayout == LastRealizedElementIndexDefault) ||
				(m_firstRealizedElementIndexHeldByLayout != FirstRealizedElementIndexDefault && m_lastRealizedElementIndexHeldByLayout != LastRealizedElementIndexDefault));

			if (index == m_firstRealizedElementIndexHeldByLayout && index == m_lastRealizedElementIndexHeldByLayout)
			{
				// First and last were pointing to the same element and that is going away.
				InvalidateRealizedIndicesHeldByLayout();
			}
			else if (index == m_firstRealizedElementIndexHeldByLayout)
			{
				// The FirstElement is going away, shrink the range by one.
				++m_firstRealizedElementIndexHeldByLayout;
			}
			else if (index == m_lastRealizedElementIndexHeldByLayout)
			{
				// Last element is going away, shrink the range by one at the end.
				--m_lastRealizedElementIndexHeldByLayout;
			}
			else
			{
				// Index is either outside the range we are keeping track of or inside the range.
				// In both these cases, we just keep the range we have. If this clear was due to 
				// a collection change, then in the CollectionChanged event, we will invalidate these guys.
			}
		}

		void ClearElementToElementFactory(UIElement element)
		{
			m_owner.OnElementClearing(element);

			if (m_owner.ItemTemplateShim)
			{
				if (!m_ElementFactoryRecycleArgs)
				{
					// Create one.
					m_ElementFactoryRecycleArgs = new ElementFactoryRecycleArgs(m_owner, new ElementFactoryRecycleArgs());
				}

				var context = m_ElementFactoryRecycleArgs;
				context.Element = element;
				context.Parent = m_owner;

				m_owner.ItemTemplateShim.RecycleElement(context);

				context.Element = null;
				context.Parent = null;
			}
			else
			{
				// No ItemTemplate to recycle to, remove the element from the children collection.
				var children = m_owner.Children;
				uint childIndex = 0;
				bool found = children.IndexOf(element, childIndex);
				if (!found)
				{
					throw new InvalidOperationException("ItemsRepeater's child not found in its Children collection.");
				}

				children.RemoveAt(childIndex);
			}

			var virtInfo = ItemsRepeater.GetVirtualizationInfo(element);
			virtInfo.MoveOwnershipToElementFactory();
			m_phaser.StopPhasing(element, virtInfo);
			if (m_lastFocusedElement == element)
			{
				// Focused element is going away. Remove the tracked last focused element
				// and pick a reasonable next focus if we can find one within the layout 
				// realized elements.
				int clearedIndex = virtInfo.Index();
				MoveFocusFromClearedIndex(clearedIndex);
			}

			REPEATER_TRACE_PERF("ElementCleared");
		}

		void MoveFocusFromClearedIndex(int clearedIndex)
		{
			UIElement focusedChild = null;
			var focusCandidate = FindFocusCandidate(clearedIndex, focusedChild);
			if (focusCandidate != null)
			{
				FocusState focusState = FocusState.Programmatic;
				if (m_lastFocusedElement is Control focusedAsControl)
				{
					focusState = focusedAsControl.FocusState;
				}

				// If the last focused element has focus, use its focus state, if not use programmatic.
				focusState = focusState == FocusState.Unfocused ? FocusState.Programmatic : focusState;
				focusCandidate.Focus(focusState);

				m_lastFocusedElement = focusedChild;
				// Add pin to hold the focused element.
				UpdatePin(focusedChild, true /* addPin */);
			}
			else
			{
				// We could not find a candiate.
				m_lastFocusedElement = null;
			}
		}

		Control FindFocusCandidate(int clearedIndex, UIElement focusedChild)
		{
			// Walk through all the children and find elements with index before and after the cleared index.
			// Note that during a delete the next element would now have the same index.
			int previousIndex = int.MinValue;
			int nextIndex = int.MaxValue;
			UIElement nextElement = null;
			UIElement previousElement = null;
			var children = m_owner.Children;
			for (uint i = 0u; i < children.Count; ++i)
			{
				var child = children.GetAt(i);
				var virtInfo = ItemsRepeater.TryGetVirtualizationInfo(child);
				if (virtInfo && virtInfo.IsHeldByLayout())
				{
					int currentIndex = virtInfo.Index();
					if (currentIndex < clearedIndex)
					{
						if (currentIndex > previousIndex)
						{
							previousIndex = currentIndex;
							previousElement = child;
						}
					}
					else if (currentIndex >= clearedIndex)
					{
						// Note that we use >= above because if we deleted the focused element, 
						// the next element would have the same index now.
						if (currentIndex < nextIndex)
						{
							nextIndex = currentIndex;
							nextElement = child;
						}
					}
				}
			}

			// Find the next element if one exists, if not use the previous element.
			// If the container itself is not focusable, find a descendent that is.
			Control focusCandidate = null;
			if (nextElement != null)
			{
				//focusedChild = nextElement as UIElement;
				focusCandidate = nextElement as Control;
				if (focusCandidate == null)
				{
					var firstFocus = FocusManager.FindFirstFocusableElement(nextElement);
					if (firstFocus != null)
					{
						focusCandidate = firstFocus as Control;
					}
				}
			}

			// TODO UNO: Case declared as useless by intellisense
			//if (focusCandidate == null && previousElement != null)
			//{
			//	focusedChild = previousElement as UIElement;
			//	focusCandidate = previousElement as Control;
			//	if (previousElement == null)
			//	{
			//		var lastFocus = FocusManager.FindLastFocusableElement(previousElement);
			//		if (lastFocus != null)
			//		{
			//			focusCandidate = lastFocus as Control;
			//		}
			//	}
			//}

			return focusCandidate;
		}

		public int GetElementIndex(VirtualizationInfo virtInfo)
		{
			if (virtInfo == null)
			{
				//Element is not a child of this ItemsRepeater.
				return -1;
			}

			return virtInfo.IsRealized || virtInfo.IsInUniqueIdResetPool ? virtInfo.Index : -1;
		}

		public void PrunePinnedElements()
		{
			EnsureEventSubscriptions();

			// Go through pinned elements and make sure they still have
			// a reason to be pinned.
			for (int i = 0; i < m_pinnedPool.Count; ++i)
			{
				var elementInfo = m_pinnedPool[i];
				var virtInfo = elementInfo.VirtualizationInfo();

				MUX_ASSERT(virtInfo.Owner == ElementOwner.PinnedPool);

				if (!virtInfo.IsPinned)
				{
					m_pinnedPool.RemoveAt(i);
					--i;

					// Pinning was the only thing keeping this element alive.
					ClearElementToElementFactory(elementInfo.PinnedElement());
				}
			}
		}

		public void UpdatePin(UIElement element, bool addPin)
		{
			var parent = CachedVisualTreeHelpers.GetParent(element);
			var child = (DependencyObject)element;

			while (parent != null)
			{
				if (parent is ItemsRepeater repeater)
				{
					var virtInfo = ItemsRepeater.GetVirtualizationInfo(child as UIElement);
					if (virtInfo.IsRealized)
					{
						if (addPin)
						{
							virtInfo.AddPin();
						}
						else if (virtInfo.IsPinned())
						{
							if (virtInfo.RemovePin() == 0)
							{
								// ElementFactory is invoked during the measure pass.
								// We will clear the element then.
								repeater.InvalidateMeasure();
							}
						}
					}
				}

				child = parent;
				parent = CachedVisualTreeHelpers.GetParent(child);
			}
		}

		public void OnItemsSourceChanged(object _, NotifyCollectionChangedEventArgs args)
		{
			// Note: For items that have been removed, the index will not be touched. It will hold
			// the old index before it was removed. It is not valid anymore.
			switch (args.Action)
			{
				case NotifyCollectionChangedAction.Add:
				{
					var newIndex = args.NewStartingIndex;
					var newCount = args.NewItems.Count;
					EnsureFirstLastRealizedIndices();
					if (newIndex <= m_lastRealizedElementIndexHeldByLayout)
					{
						m_lastRealizedElementIndexHeldByLayout += newCount;
						var children = m_owner.Children;
						var childCount = children.Count;
						for (uint i = 0u; i < childCount; ++i)
						{
							var element = children.GetAt(i);
							var virtInfo = ItemsRepeater.GetVirtualizationInfo(element);
							var dataIndex = virtInfo.Index();

							if (virtInfo.IsRealized() && dataIndex >= newIndex)
							{
								UpdateElementIndex(element, virtInfo, dataIndex + newCount);
							}
						}
					}
					else
					{
						// Indices held by layout are not affected
						// We could still have items in the pinned elements that need updates. This is usually a very small vector.
						for (int i = 0; i < m_pinnedPool.Count; ++i)
						{
							var elementInfo = m_pinnedPool[i];
							var virtInfo = elementInfo.VirtualizationInfo();
							var dataIndex = virtInfo.Index();

							if (virtInfo.IsRealized() && dataIndex >= newIndex)
							{
								var element = elementInfo.PinnedElement();
								UpdateElementIndex(element, virtInfo, dataIndex + newCount);
							}
						}
					}

					break;
				}

				case NotifyCollectionChangedAction.Replace:
				{
					// Requirement: oldStartIndex == newStartIndex. It is not a replace if this is not true.
					// Two cases here
					// case 1: oldCount == newCount 
					//         indices are not affected. nothing to do here.  
					// case 2: oldCount != newCount
					//         Replaced with less or more items. This is like an insert or remove
					//         depending on the counts.
					var oldStartIndex = args.OldStartingIndex;
					var newStartingIndex = args.NewStartingIndex;
					var oldCount = (int)(args.OldItems.Count);
					var newCount = (int)(args.NewItems.Count);
					if (oldStartIndex != newStartingIndex)
					{
						throw new InvalidOperationException("Replace is only allowed with OldStartingIndex equals to NewStartingIndex.");
					}

					if (oldCount == 0)
					{
						throw new InvalidOperationException("Replace notification with args.OldItemsCount value of 0 is not allowed. Use Insert action instead.");
					}

					if (newCount == 0)
					{
						throw new InvalidOperationException("Replace notification with args.NewItemCount value of 0 is not allowed. Use Remove action instead.");
					}

					int countChange = newCount - oldCount;
					if (countChange != 0)
					{
						// countChange > 0 : countChange items were added
						// countChange < 0 : -countChange  items were removed
						var children = m_owner.Children;
						for (uint i = 0u; i < children.Count; ++i)
						{
							var element = children.GetAt(i);
							var virtInfo = ItemsRepeater.GetVirtualizationInfo(element);
							var dataIndex = virtInfo.Index();

							if (virtInfo.IsRealized())
							{
								if (dataIndex >= oldStartIndex + oldCount)
								{
									UpdateElementIndex(element, virtInfo, dataIndex + countChange);
								}
							}
						}

						EnsureFirstLastRealizedIndices();
						m_lastRealizedElementIndexHeldByLayout += countChange;
					}

					break;
				}

				case NotifyCollectionChangedAction.Remove:
				{
					var oldStartIndex = args.OldStartingIndex;
					var oldCount = (int)(args.OldItems.Count);
					var children = m_owner.Children;
					for (uint i = 0u; i < children.Count; ++i)
					{
						var element = children.GetAt(i);
						var virtInfo = ItemsRepeater.GetVirtualizationInfo(element);
						var dataIndex = virtInfo.Index();

						if (virtInfo.IsRealized())
						{
							if (virtInfo.AutoRecycleCandidate() && oldStartIndex <= dataIndex && dataIndex < oldStartIndex + oldCount)
							{
								// If we are doing the mapping, remove the element who's data was removed.
								m_owner.ClearElementImpl(element);
							}
							else if (dataIndex >= (oldStartIndex + oldCount))
							{
								UpdateElementIndex(element, virtInfo, dataIndex - oldCount);
							}
						}
					}

					InvalidateRealizedIndicesHeldByLayout();
					break;
				}

				case NotifyCollectionChangedAction.Reset:
					// If we get multiple resets back to back before
					// running layout, we dont have to clear all the elements again.         
					if (!m_isDataSourceStableResetPending)
					{
						// There should be no elements in the reset pool at this time.
						MUX_ASSERT(m_resetPool.IsEmpty());

						if (m_owner.ItemsSourceView.HasKeyIndexMapping())
						{
							m_isDataSourceStableResetPending = true;
						}

						// Walk through all the elements and make sure they are cleared, they will go into
						// the stable id reset pool.
						var children = m_owner.Children;
						for (uint i = 0u; i < children.Count; ++i)
						{
							var element = children.GetAt(i);
							var virtInfo = ItemsRepeater.GetVirtualizationInfo(element);
							if (virtInfo.IsRealized() && virtInfo.AutoRecycleCandidate())
							{
								m_owner.ClearElementImpl(element);
							}
						}
					}

					InvalidateRealizedIndicesHeldByLayout();

					break;
			}
		}

		void EnsureFirstLastRealizedIndices()
		{
			if (m_firstRealizedElementIndexHeldByLayout == FirstRealizedElementIndexDefault)
			{
				// This will ensure that the indexes are updated.
				var element = GetElementIfAlreadyHeldByLayout(0);
			}
		}

		public void OnLayoutChanging()
		{
			if (m_owner.ItemsSourceView &&
				m_owner.ItemsSourceView.HasKeyIndexMapping)
			{
				m_isDataSourceStableResetPending = true;
			}
		}

		public void OnOwnerArranged()
		{
			if (m_isDataSourceStableResetPending)
			{
				m_isDataSourceStableResetPending = false;

				foreach (var entry in m_resetPool)
				{
					// TODO: Task 14204306: ItemsRepeater: Find better focus candidate when focused element is deleted in the ItemsSource.
					// Focused element is getting cleared. Need to figure out semantics on where
					// focus should go when the focused element is removed from the data collection.
					ClearElement(entry.Value, true /* isClearedDueToCollectionChange */);
				}

				m_resetPool.Clear();

				// Flush the realized indices once the stable reset pool is cleared to start fresh.
				InvalidateRealizedIndicesHeldByLayout();
			}
		}

		#region GetElement providers

		// We optimize for the case where index is not realized to return null as quickly as we can.
		// Flow layouts manage containers on their own and will never ask for an index that is already realized.
		// If an index that is realized is requested by the layout, we unfortunately have to walk the
		// children. Not ideal, but a reasonable default to provide consistent behavior between virtualizing
		// and non-virtualizing hosts.
		UIElement GetElementIfAlreadyHeldByLayout(int index)
		{
			UIElement element = null;

			bool cachedFirstLastIndicesInvalid = m_firstRealizedElementIndexHeldByLayout == FirstRealizedElementIndexDefault;
			MUX_ASSERT(!cachedFirstLastIndicesInvalid || m_lastRealizedElementIndexHeldByLayout == LastRealizedElementIndexDefault);

			bool isRequestedIndexInRealizedRange = (m_firstRealizedElementIndexHeldByLayout <= index && index <= m_lastRealizedElementIndexHeldByLayout);

			if (cachedFirstLastIndicesInvalid || isRequestedIndexInRealizedRange)
			{
				// Both First and Last indices need to be valid or default.
				MUX_ASSERT((m_firstRealizedElementIndexHeldByLayout == FirstRealizedElementIndexDefault && m_lastRealizedElementIndexHeldByLayout == LastRealizedElementIndexDefault) ||
					(m_firstRealizedElementIndexHeldByLayout != FirstRealizedElementIndexDefault && m_lastRealizedElementIndexHeldByLayout != LastRealizedElementIndexDefault));

				var children = m_owner.Children;
				for (uint i = 0u; i < children.Count; ++i)
				{
					var child = children.GetAt(i);
					var virtInfo = ItemsRepeater.TryGetVirtualizationInfo(child);
					if (virtInfo && virtInfo.IsHeldByLayout())
					{
						// Only give back elements held by layout. If someone else is holding it, they will be served by other methods.
						int childIndex = virtInfo.Index();
						m_firstRealizedElementIndexHeldByLayout = Math.Min(m_firstRealizedElementIndexHeldByLayout, childIndex);
						m_lastRealizedElementIndexHeldByLayout = Math.Max(m_lastRealizedElementIndexHeldByLayout, childIndex);
						if (virtInfo.Index() == index)
						{
							element = child;
							// If we have valid first/last indices, we don't have to walk the rest, but if we 
							// do not, then we keep walking through the entire children collection to get accurate
							// indices once.
							if (!cachedFirstLastIndicesInvalid)
							{
								break;
							}
						}
					}
				}
			}

			return element;
		}

		UIElement GetElementFromUniqueIdResetPool(int index)
		{
			UIElement element = null;
			// See if you can get it from the reset pool.
			if (m_isDataSourceStableResetPending)
			{
				element = m_resetPool.Remove(index);
				if (element != null)
				{
					// Make sure that the index is updated to the current one
					var virtInfo = ItemsRepeater.GetVirtualizationInfo(element);
					virtInfo.MoveOwnershipToLayoutFromUniqueIdResetPool();
					UpdateElementIndex(element, virtInfo, index);

					// Update realized indices
					m_firstRealizedElementIndexHeldByLayout = Math.Min(m_firstRealizedElementIndexHeldByLayout, index);
					m_lastRealizedElementIndexHeldByLayout = Math.Max(m_lastRealizedElementIndexHeldByLayout, index);
				}
			}

			return element;
		}

		UIElement GetElementFromPinnedElements(int index)
		{
			UIElement element = null;

			// See if you can find something among the pinned elements.
			for (int i = 0; i < m_pinnedPool.Count; ++i)
			{
				var elementInfo = m_pinnedPool[i];
				var virtInfo = elementInfo.VirtualizationInfo();

				if (virtInfo.Index() == index)
				{
					m_pinnedPool.RemoveAt(i);
					element = elementInfo.PinnedElement();
					elementInfo.VirtualizationInfo().MoveOwnershipToLayoutFromPinnedPool();

					// Update realized indices
					m_firstRealizedElementIndexHeldByLayout = Math.Min(m_firstRealizedElementIndexHeldByLayout, index);
					m_lastRealizedElementIndexHeldByLayout = Math.Max(m_lastRealizedElementIndexHeldByLayout, index);
					break;
				}
			}

			return element;
		}

		// There are several cases handled here with respect to which element gets returned and when DataContext is modified.
		//
		// 1. If there is no ItemTemplate:
		//    1.1 If data is a UIElement . the data is returned
		//    1.2 If data is not a UIElement . a default DataTemplate is used to fetch element and DataContext is set to data**
		//
		// 2. If there is an ItemTemplate:
		//    2.1 If data is not a FrameworkElement . Element is fetched from ElementFactory and DataContext is set to the data**
		//    2.2 If data is a FrameworkElement:
		//        2.2.1 If Element returned by the ElementFactory is the same as the data . Element (a.k.a. data) is returned as is
		//        2.2.2 If Element returned by the ElementFactory is not the same as the data
		//                 . Element that is fetched from the ElementFactory is returned and
		//                    DataContext is set to the data's DataContext (if it exists), otherwise it is set to the data itself**
		//
		// **data context is set only if no x:Bind was used. ie. No data template component on the root.
		UIElement GetElementFromElementFactory(int index)
		{
			// The view generator is the provider of last resort.
			var  data = m_owner.ItemsSourceView.GetAt(index);

			var providedElementFactory = m_owner.ItemTemplateShim;
			//var element = [this, data, index, providedElementFactory = m_owner.ItemTemplateShim()]()
			{
				if (providedElementFactory != null)
				{
					if (data is UIElement dataAsElement)
					{
						return dataAsElement;
					}
				}

				var elementFactory = providedElementFactory;
				{
					if (!providedElementFactory)
					{
						// If no ItemTemplate was provided, use a default
						(var factory  = XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding}'/></DataTemplate>") as DataTemplate);
						m_owner.ItemTemplate(factory);
						return m_owner.ItemTemplateShim();
					}

					return providedElementFactory;
				}
				();

				var  args  = [this]()
				{
					if (!m_ElementFactoryGetArgs)
					{
						m_ElementFactoryGetArgs = ElementFactoryGetArgs(m_owner, *new ElementFactoryGetArgs());
					}

					return m_ElementFactoryGetArgs;
				}
				();

				var scopeGuard = gsl.finally([args]()
				{
					args.Data(null);
					args.Parent(null);
				});

				args.Data(data);
				args.Parent(*m_owner);
				(args as ElementFactoryGetArgs).Index(index);

				return elementFactory.GetElement(args);
			}
			();

			var virtInfo = ItemsRepeater.TryGetVirtualizationInfo(element);
			if (!virtInfo)
			{
				virtInfo = ItemsRepeater.CreateAndInitializeVirtualizationInfo(element);
				REPEATER_TRACE_PERF("ElementCreated");
			}
			else
			{
				// View obtained from ElementFactory already has a VirtualizationInfo attached to it
				// which means that the element has been recycled and not created from scratch.
				REPEATER_TRACE_PERF("ElementRecycled");
			}

			if (data != element)
			{
				// Prepare the element
				// If we are phasing, run phase 0 before setting DataContext. If phase 0 is not 
				// run before setting DataContext, when setting DataContext all the phases will be
				// run in the OnDataContextChanged handler in code generated by the xaml compiler (code-gen).
				var extension = CachedVisualTreeHelpers.GetDataTemplateComponent(element);
				if (extension != null)
				{
					// Clear out old data. 
					extension.Recycle();
					int nextPhase = VirtualizationInfo.PhaseReachedEnd;
					// Run Phase 0
					extension.ProcessBindings(data, index, 0 /* currentPhase */, nextPhase);

					// Setup phasing information, so that Phaser can pick up any pending phases left.
					// Update phase on virtInfo. Set data and templateComponent only if x:Phase was used.
					virtInfo.UpdatePhasingInfo(nextPhase, nextPhase > 0 ? data : null, nextPhase > 0 ? extension : null);
				}
				else if (var elementAsFE = element.try_as<FrameworkElement>())
				{
					// Set data context only if no x:Bind was used. ie. No data template component on the root.
					// If the passed in data is a UIElement and is different from the element returned by 
					// the template factory then we need to propagate the DataContext.
					// Otherwise just set the DataContext on the element as the data.
					var  elementDataContext  = [this, data]()
					{
						if (var dataAsElement  = data.try_as<FrameworkElement>())
						{
							if (var dataDataContext  = dataAsElement.DataContext())
							{
								return dataDataContext;
							}
						}
						return data;
					}
					();

					elementAsFE.DataContext(elementDataContext);
				}
				else
				{
					MUX_ASSERT("Element returned by factory is not a FrameworkElement!");
				}
			}

			virtInfo.MoveOwnershipToLayoutFromElementFactory(
				index,
				/* uniqueId: */
				m_owner.ItemsSourceView.HasKeyIndexMapping() ? m_owner.ItemsSourceView.KeyFromIndex(index) : hstring{
			});

			// The view generator is the only provider that prepares the element.
			var repeater = m_owner;

			// Add the element to the children collection here before raising OnElementPrepared so 
			// that handlers can walk up the tree in case they want to find their IndexPath in the 
			// nested case.
			var children = repeater.Children;
			if (CachedVisualTreeHelpers.GetParent(element) != (DependencyObject)(*repeater))
			{
				children.Append(element);
			}

			repeater.AnimationManager().OnElementPrepared(element);

			repeater.OnElementPrepared(element, index);

			if (data != element)
			{
				m_phaser.PhaseElement(element, virtInfo);
			}

			// Update realized indices
			m_firstRealizedElementIndexHeldByLayout = Math.Min(m_firstRealizedElementIndexHeldByLayout, index);
			m_lastRealizedElementIndexHeldByLayout = Math.Max(m_lastRealizedElementIndexHeldByLayout, index);

			return element;
		}

		#endregion

		#region ClearElement handlers

		bool ClearElementToUniqueIdResetPool(UIElement element, VirtualizationInfo virtInfo)
		{
			if (m_isDataSourceStableResetPending)
			{
				m_resetPool.Add(element);
				virtInfo.MoveOwnershipToUniqueIdResetPoolFromLayout();
			}

			return m_isDataSourceStableResetPending;
		}

		bool ClearElementToAnimator(UIElement element, VirtualizationInfo virtInfo)
		{
			bool cleared = m_owner.AnimationManager.ClearElement(element);
			if (cleared)
			{
				int clearedIndex = virtInfo.Index();
				virtInfo.MoveOwnershipToAnimator();
				if (m_lastFocusedElement == element)
				{
					// Focused element is going away. Remove the tracked last focused element
					// and pick a reasonable next focus if we can find one within the layout 
					// realized elements.
					MoveFocusFromClearedIndex(clearedIndex);
				}

			}

			return cleared;
		}

		bool ClearElementToPinnedPool(UIElement element, VirtualizationInfo virtInfo, bool isClearedDueToCollectionChange)
		{
			bool moveToPinnedPool =
				!isClearedDueToCollectionChange && virtInfo.IsPinned;

			if (moveToPinnedPool)
			{
#ifdef _DEBUG
				for (int i = 0; i < m_pinnedPool.Count; ++i)
				{
					MUX_ASSERT(m_pinnedPool[i].PinnedElement() != element);
				}

#endif
				m_pinnedPool.push_back(PinnedElementInfo(m_owner, element));
				virtInfo.MoveOwnershipToPinnedPool();
			}

			return moveToPinnedPool;
		}

		#endregion

		void UpdateFocusedElement()
		{
			UIElement focusedElement = null;

			if (FocusManager.GetFocusedElement() is DependencyObject child)
			{
				var parent = CachedVisualTreeHelpers.GetParent(child);
				var owner = (UIElement)m_owner;

				// Find out if the focused element belongs to one of our direct
				// children.
				while (parent != null)
				{
					if (parent is ItemsRepeater repeater)
					{
						var element = child as UIElement;
						if (repeater == owner && ItemsRepeater.GetVirtualizationInfo(element).IsRealized())
						{
							focusedElement = element;
						}

						break;
					}

					child = parent;
					parent = CachedVisualTreeHelpers.GetParent(child);
				}
			}

			// If the focused element has changed,
			// we need to unpin the old one and pin the new one.
			if (m_lastFocusedElement != focusedElement)
			{
				if (m_lastFocusedElement)
				{
					UpdatePin(m_lastFocusedElement, false /* addPin */);
				}

				if (focusedElement)
				{
					UpdatePin(focusedElement, true /* addPin */);
				}

				m_lastFocusedElement = focusedElement;
			}
		}

		void OnFocusChanged(object _, RoutedEventArgs args)
		{
			UpdateFocusedElement();
		}

		void EnsureEventSubscriptions()
		{
			if (m_gotFocus == null)
			{
				MUX_ASSERT(m_lostFocus == null);
				m_gotFocus = m_owner.GotFocus += OnFocusChanged;
				m_lostFocus = m_owner.LostFocus += OnFocusChanged;
			}
		}

		void UpdateElementIndex(UIElement element, VirtualizationInfo virtInfo, int index)
		{
			var oldIndex = virtInfo.Index;
			if (oldIndex != index)
			{
				virtInfo.UpdateIndex(index);
				m_owner.OnElementIndexChanged(element, oldIndex, index);
			}
		}

		void InvalidateRealizedIndicesHeldByLayout()
		{
			m_firstRealizedElementIndexHeldByLayout = FirstRealizedElementIndexDefault;
			m_lastRealizedElementIndexHeldByLayout = LastRealizedElementIndexDefault;
		}

		ViewManager.PinnedElementInfo.PinnedElementInfo(ITrackerHandleManager* owner,  UIElement element) :

		m_pinnedElement(owner, element),

		m_virtInfo(owner, ItemsRepeater.GetVirtualizationInfo(element))
		{
		}
	}
}
