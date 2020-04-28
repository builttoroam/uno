declare const require: any;

namespace Uno.UI {
	export interface LottieAnimationProperties {
		elementId: number;
		jsonPath: string;
		autoplay: boolean;
		stretch: string;
		rate: number;
	}

	export interface RunningLottieAnimation {
		animation: Lottie.AnimationItem;
		properties: LottieAnimationProperties;
	}

	export class Lottie {
		private static _player: LottiePlayer;
		private static _runningAnimations: { [id: number]: RunningLottieAnimation } = {};

		public static setAnimationProperties(newProperties: LottieAnimationProperties): string {
			const elementId = newProperties.elementId;

			this.withPlayer(p => {
				let currentAnimation = this._runningAnimations[elementId];

				if (!currentAnimation || this.needNewPlayerAnimation(currentAnimation.properties, newProperties)) {
					// Here we need a new player animation
					// (some property changes required a new animation)
					currentAnimation = this.createAnimation(newProperties);
				}

				this.updateProperties(currentAnimation, newProperties);
			});

			return "ok";
		}

		public static stop(elementId: number): string {
			this.withPlayer(p => {
				const a = this._runningAnimations[elementId].animation;
				a.stop();
				this.raiseState(a);
			});

			return "ok";
		}

		public static play(elementId: number, looped: boolean): string {
			this.withPlayer(p => {
				const a = this._runningAnimations[elementId].animation;
				a.loop = looped;
				a.play();
				this.raiseState(a);
			});

			return "ok";
		}

		public static kill(elementId: number): string {
			this.withPlayer(p => {
				this._runningAnimations[elementId].animation.destroy();
				delete this._runningAnimations[elementId];
			});

			return "ok";
		}

		public static pause(elementId: number): string {
			this.withPlayer(p => {
				const a = this._runningAnimations[elementId].animation;
				a.pause();
				this.raiseState(a);
			});

			return "ok";
		}

		public static resume(elementId: number): string {
			this.withPlayer(p => {
				const a = this._runningAnimations[elementId].animation;
				a.play();
				this.raiseState(a);
			});

			return "ok";
		}

		public static setProgress(elementId: number, progress: number): string {
			this.withPlayer(p => {
				const animation = this._runningAnimations[elementId].animation;
				const frames = animation.getDuration(true);
				const frame = frames * progress;
				animation.goToAndStop(frame, true);
				this.raiseState(animation);

			});

			return "ok";
		}

		public static getAnimationState(elementId: number): string {
			const animation = this._runningAnimations[elementId].animation;

			const state = this.getStateString(animation);

			return state;
		}

		private static needNewPlayerAnimation(current: LottieAnimationProperties, newProperties: LottieAnimationProperties): boolean {

			if (current.jsonPath !== newProperties.jsonPath) {
				return true;
			}

			if (newProperties.stretch !== current.stretch) {
				return true;
			}
			if (newProperties.autoplay !== current.autoplay) {
				return true;
			}
			if (newProperties.jsonPath !== current.jsonPath) {
				return true;
			}

			return false;
		}

		private static updateProperties(
			runningAnimation: RunningLottieAnimation,
			newProperties: LottieAnimationProperties) {

			const animation = runningAnimation.animation;
			const runningProperties = runningAnimation.properties;

			if (runningProperties == null || newProperties.rate != runningProperties.rate) {
				animation.setSpeed(newProperties.rate);
			}

			runningAnimation.properties = newProperties;
		}

		private static createAnimation(properties: LottieAnimationProperties): RunningLottieAnimation {
			const existingAnimation = this._runningAnimations[properties.elementId];
			if (existingAnimation) {
				// destroy any previous animation
				existingAnimation.animation.destroy();
				existingAnimation.animation = null;
			}

			const config = this.getPlayerConfig(properties);
			const animation = this._player.loadAnimation(config);

			const runningAnimation = {
				animation: animation,
				properties: properties
			};

			this._runningAnimations[properties.elementId] = runningAnimation;

			(animation as any).addEventListener("complete", (e: any) => {
				Lottie.raiseState(animation);
			});

			(animation as any).addEventListener("loopComplete", (e: any) => {
				Lottie.raiseState(animation);
			});

			(animation as any).addEventListener("segmentStart", (e: any) => {
				Lottie.raiseState(animation);
			});

			(animation as any).addEventListener("data_ready", (e: any) => {
				Lottie.raiseState(animation);
			});

			Lottie.raiseState(animation);

			return runningAnimation;
		}

		private static getStateString(animation: Lottie.AnimationItem): string {
			const duration = animation.getDuration(false);

			const state = `${animation.animationData.w}|${animation.animationData.h}|${animation.isLoaded}|${animation.isPaused}|${duration}`;
			return state;
		}

		private static raiseState(animation: Lottie.AnimationItem) {
			const element = animation.wrapper;
			const state = this.getStateString(animation);

			element.dispatchEvent(new CustomEvent("lottie_state", { detail: state }));
		}

		private static getPlayerConfig(properties: LottieAnimationProperties): Lottie.AnimationConfig {
			let scaleMode = "none";
			switch (properties.stretch) {
				case "Uniform":
					scaleMode = "xMidYMid meet";
					break;
				case "UniformToFill":
					scaleMode = "xMidYMid slice";
					break;
				case "Fill":
					scaleMode = "noScale";
					break;
			}

			const containerElement = (Uno.UI as any).WindowManager.current.getView(properties.elementId);

			// https://github.com/airbnb/lottie-web/wiki/loadAnimation-options
			const playerConfig = {
				path: properties.jsonPath,
				loop: true,
				autoplay: properties.autoplay,
				name: `Lottie-${properties.elementId}`,
				renderer: "svg", // https://github.com/airbnb/lottie-web/wiki/Features
				container: containerElement,
				rendererSettings: {
					// https://github.com/airbnb/lottie-web/wiki/Renderer-Settings
					preserveAspectRatio: scaleMode
				}
			};

			return playerConfig;
		}

		private static withPlayer(action: (player: LottiePlayer) => void): void {
			if (Lottie._player) {
				action(Lottie._player);
			} else {
				require(["lottie"], (p: LottiePlayer) => {
					if (!Lottie._player) {
						Lottie._player = p;
					}
					action(p);
				});
			}
		}
	}
}
