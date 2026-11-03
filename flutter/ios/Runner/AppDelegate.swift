import Flutter
import UIKit
import StoreKit
import workmanager_apple

@main
@objc class AppDelegate: FlutterAppDelegate {
  // Overlay shown over the window while backgrounded so the app-switcher snapshot
  // never exposes financial content when biometric app-lock is on (#biometric).
  private var secureBlurView: UIView?

  override func application(
    _ application: UIApplication,
    didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?
  ) -> Bool {
    // StoreKit storefront channel: lets Dart read the App Store country code so
    // the external Stripe Checkout link-out is offered only to US storefronts
    // (App Review 3.1.1/3.1.3). Returns the 3-letter code (e.g. "USA") or nil.
    if let controller = window?.rootViewController as? FlutterViewController {
      let storefrontChannel = FlutterMethodChannel(
        name: "com.buoyai.buoyAi/storefront",
        binaryMessenger: controller.binaryMessenger)
      storefrontChannel.setMethodCallHandler { call, result in
        guard call.method == "getCountryCode" else {
          result(FlutterMethodNotImplemented)
          return
        }
        if #available(iOS 13.0, *) {
          result(SKPaymentQueue.default().storefront?.countryCode)
        } else {
          result(nil)
        }
      }

      // Biometric app-lock (#biometric): Dart pushes the enabled flag (on toggle
      // and at startup) into UserDefaults so applicationWillResignActive can decide
      // whether to obscure the pre-background snapshot.
      let secureWindowChannel = FlutterMethodChannel(
        name: "com.buoyai/secure_window",
        binaryMessenger: controller.binaryMessenger)
      secureWindowChannel.setMethodCallHandler { call, result in
        guard call.method == "setAppLockEnabled" else {
          result(FlutterMethodNotImplemented)
          return
        }
        let enabled = (call.arguments as? [String: Any])?["enabled"] as? Bool ?? false
        UserDefaults.standard.set(enabled, forKey: "app_lock_enabled")
        result(nil)
      }
    }
    GeneratedPluginRegistrant.register(with: self)

    // Offline background sync (#493): register the BGTaskScheduler identifier so
    // iOS can run the periodic outbox drain + cache warm. The identifier must
    // match Info.plist's BGTaskSchedulerPermittedIdentifiers and the Dart
    // registerPeriodicTask call (kBuoyaiSyncTask). iOS decides the actual cadence.
    WorkmanagerPlugin.registerPeriodicTask(
      withIdentifier: "com.buoyai.buoyAi.sync",
      frequency: NSNumber(value: 30 * 60)
    )

    return super.application(application, didFinishLaunchingWithOptions: launchOptions)
  }

  // iOS captures the app-switcher snapshot right after willResignActive, so add
  // the blur overlay here (only when app-lock is enabled) and remove it once the
  // app is active again.
  override func applicationWillResignActive(_ application: UIApplication) {
    super.applicationWillResignActive(application)
    guard UserDefaults.standard.bool(forKey: "app_lock_enabled") else { return }
    guard let window = self.window, secureBlurView == nil else { return }
    let blur = UIVisualEffectView(effect: UIBlurEffect(style: .systemMaterial))
    blur.frame = window.bounds
    blur.autoresizingMask = [.flexibleWidth, .flexibleHeight]
    window.addSubview(blur)
    secureBlurView = blur
  }

  override func applicationDidBecomeActive(_ application: UIApplication) {
    super.applicationDidBecomeActive(application)
    secureBlurView?.removeFromSuperview()
    secureBlurView = nil
  }
}
