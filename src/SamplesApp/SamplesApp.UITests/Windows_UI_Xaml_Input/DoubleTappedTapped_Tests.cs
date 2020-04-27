﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using SamplesApp.UITests.TestFramework;
using Uno.UITest.Helpers;
using Uno.UITest.Helpers.Queries;
using Uno.UITests.Helpers;

namespace SamplesApp.UITests.Windows_UI_Xaml_Input
{
	public class DoubleTapped_Tests : SampleControlUITestBase
	{
		private const string _xamlTestPage = "UITests.Shared.Windows_UI_Input.GestureRecognizerTests.DoubleTappedTests";

		[Test]
		[AutoRetry]
		public void When_Basic()
		{
			Run(_xamlTestPage);

			const string targetName = "Basic_Target";
			const int tapX = 10, tapY = 10;

			// Double tap the target
			var target = _app.WaitForElement(targetName).Single().Rect;
			_app.DoubleTapCoordinates(target.X + tapX, target.Y + tapY);

			var result = GestureResult.Get(_app.Marked("LastDoubleTapped"));
			result.Element.Should().Be(targetName);
			((int)result.X).Should().Be(tapX);
			((int)result.Y).Should().Be(tapY);
		}

		[Test]
		[AutoRetry]
		public void When_Transformed()
		{
			Run(_xamlTestPage);

			const string parentName = "Transformed_Parent";
			const string targetName = "Transformed_Target";

#if __WASM__ // Workaournd until https://github.com/unoplatform/Uno.UITest/pull/35 is merged
			var parent = _app.Query(q => q.Marked(parentName)).Single().Rect;
			var target = _app.Query(q => q.Marked(targetName)).Single().Rect;
#else
			var parent = _app.Query(q => q.All().Marked(parentName)).Single().Rect;
			var target = _app.Query(q => q.All().Marked(targetName)).Single().Rect;
#endif

			// Double tap the target
			_app.DoubleTapCoordinates(parent.Right - target.Width, parent.Bottom - 3);

			var result = GestureResult.Get(_app.Marked("LastDoubleTapped"));
			result.Element.Should().Be(targetName);
		}

		[Test]
		[AutoRetry]
		[ActivePlatforms(Platform.Android, Platform.iOS)] // We cannot scroll to reach the target on WASM yet
		public void When_InScroll()
		{
			Run(_xamlTestPage);

			const string targetName = "InScroll_Target";
			const int tapX = 10, tapY = 10;

			// Scroll to make the target visible
			var scroll = _app.WaitForElement("InScroll_ScrollViewer").Single().Rect;
			_app.DragCoordinates(scroll.Right - 3, scroll.Bottom - 3, 0, 0);

			// Double tap the target
			var target = _app.WaitForElement(targetName).Single();
			_app.DoubleTapCoordinates(target.Rect.X + tapX, target.Rect.Y + tapY);

			var result = GestureResult.Get(_app.Marked("LastDoubleTapped"));
			result.Element.Should().Be(targetName);
			((int)result.X).Should().Be(tapX);
			((int)result.Y).Should().Be(tapY);
		}

		[Test]
		[AutoRetry]
		[Ignore("https://github.com/unoplatform/uno/issues/2739")]
		public void When_InListViewWithItemClick()
		{
			Run(_xamlTestPage);

			const string targetName = "ListViewWithItemClick";

			// Scroll a bit in the ListView
			var target = _app.WaitForElement(targetName).Single().Rect;
			_app.DragCoordinates(target.CenterX, target.Bottom - 3, target.CenterX, target.Y + 3);

			// Tap and hold an item
			_app.DoubleTapCoordinates(target.CenterX, target.CenterY - 5);

			var result = GestureResult.Get(_app.Marked("LastDoubleTapped"));
			var expectedItem = AppInitializer.GetLocalPlatform() == Platform.Browser
				? "Item_1" // We were not able to scroll on WASM!
				: "Item_3";
			result.Element.Should().Be(expectedItem);
		}

		[Test]
		[AutoRetry]
		[Ignore("https://github.com/unoplatform/uno/issues/2739")]
		public void When_InListViewWithoutItemClick()
		{
			Run(_xamlTestPage);

			const string targetName = "ListViewWithoutItemClick";

			// Scroll a bit in the ListView
			var target = _app.WaitForElement(targetName).Single().Rect;
			_app.DragCoordinates(target.CenterX, target.Bottom - 3, target.CenterX, target.Y + 3);

			// Tap and hold an item
			_app.DoubleTapCoordinates(target.CenterX, target.CenterY - 5);

			var result = GestureResult.Get(_app.Marked("LastDoubleTapped"));
			var expectedItem = AppInitializer.GetLocalPlatform() == Platform.Browser
				? "Item_1" // We were not able to scroll on WASM!
				: "Item_3";
			result.Element.Should().Be(expectedItem);
		}
	}
}
