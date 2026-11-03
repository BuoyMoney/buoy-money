package com.buoyai.buoy_ai

import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build
import android.os.Bundle
import android.view.WindowManager
import io.flutter.embedding.android.FlutterFragmentActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel

// Using FlutterFragmentActivity instead of FlutterActivity
// to support the local_auth plugin for biometric authentication.
class MainActivity : FlutterFragmentActivity() {
    private val secureWindowChannel = "com.buoyai/secure_window"

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        createNotificationChannel()
    }

    // Push notifications (#190): the channel FCM's manifest meta-data
    // (default_notification_channel_id) points at. Created natively so no
    // local-notifications plugin is needed; creating an existing channel is a no-op.
    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val channel = NotificationChannel(
            "buoyai_alerts",
            "Alerts",
            NotificationManager.IMPORTANCE_HIGH,
        ).apply {
            description = "Bill reminders, budget alerts, and account notifications"
        }
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)

        // Biometric app-lock (#biometric): the Dart side pushes the enabled flag
        // here (on toggle and at startup) so we can protect the window snapshot.
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, secureWindowChannel)
            .setMethodCallHandler { call, result ->
                when (call.method) {
                    "setAppLockEnabled" -> {
                        setSecure(call.argument<Boolean>("enabled") ?: false)
                        result.success(null)
                    }
                    else -> result.notImplemented()
                }
            }
    }

    /// Set/clear FLAG_SECURE on the window. FLAG_SECURE blocks screenshots AND the
    /// recents/app-switcher thumbnail while biometric app-lock is on. Window flags
    /// must be mutated on the UI thread.
    private fun setSecure(enabled: Boolean) {
        runOnUiThread {
            if (enabled) {
                window.setFlags(
                    WindowManager.LayoutParams.FLAG_SECURE,
                    WindowManager.LayoutParams.FLAG_SECURE,
                )
            } else {
                window.clearFlags(WindowManager.LayoutParams.FLAG_SECURE)
            }
        }
    }
}
