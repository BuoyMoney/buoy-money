import java.util.Properties
import java.io.FileInputStream

plugins {
    id("com.android.application")
    // START: FlutterFire Configuration
    id("com.google.gms.google-services")
    // END: FlutterFire Configuration
    id("kotlin-android")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

// Local recording builds only (see the profile build type below).
val buoyLocalBuild = (project.findProperty("buoyLocalBuild") as String?) == "true"
// Profile builds only: -PbuoyAppSuffix=selfhost -PbuoyAppLabel="BuoyAI local" gives a test build its own
// package id and launcher name, so it can sit beside the Play app and the .film/.local builds.
val buoyAppSuffix = project.findProperty("buoyAppSuffix") as String?
val buoyAppLabel = project.findProperty("buoyAppLabel") as String?


// Load keystore properties for release signing
val keystorePropertiesFile = rootProject.file("key.properties")
val keystoreProperties = Properties()
if (keystorePropertiesFile.exists()) {
    keystoreProperties.load(FileInputStream(keystorePropertiesFile))
}

android {
    namespace = "com.buoyai.buoy_ai"
    compileSdk = flutter.compileSdkVersion
    ndkVersion = flutter.ndkVersion

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = JavaVersion.VERSION_17.toString()
    }

    defaultConfig {
        applicationId = "com.buoyai.buoy_ai"
        manifestPlaceholders["appLabel"] = "Buoy Money"
        minSdk = flutter.minSdkVersion
        targetSdk = flutter.targetSdkVersion
        // versionCode is derived from the version name (major*10000 + minor*100 + patch)
        // so it always tracks the release version and can never collide on a Play upload:
        //   1.3.9 -> 10309, 1.3.11 -> 10311, 1.3.12 -> 10312.
        // Bumping the version in pubspec.yaml is the single source of truth; no manual
        // --build-number needed (it no longer affects versionCode). Assumes minor/patch < 100.
        versionCode = flutter.versionName.split("+")[0].split(".").let { parts ->
            (parts.getOrNull(0)?.toIntOrNull() ?: 0) * 10000 +
                (parts.getOrNull(1)?.toIntOrNull() ?: 0) * 100 +
                (parts.getOrNull(2)?.toIntOrNull() ?: 0)
        }
        versionName = flutter.versionName
    }

    signingConfigs {
        create("release") {
            if (keystorePropertiesFile.exists()) {
                keyAlias = keystoreProperties["keyAlias"] as String
                keyPassword = keystoreProperties["keyPassword"] as String
                storeFile = file(keystoreProperties["storeFile"] as String)
                storePassword = keystoreProperties["storePassword"] as String
            }
        }
    }

    buildTypes {
        // Profile builds get their own package id so a staging-pointed filming build
        // can sit NEXT TO the Play-store app
        // on a real device instead of replacing it. Side effects: FCM push and Google
        // sign-in don't work under the suffixed id (not registered in Firebase/GCP);
        // demo accounts use password login, and the one push-dependent shot is filmed
        // on the prod app instead.
        maybeCreate("profile")
        getByName("profile") {
            // -PbuoyLocalBuild=true builds the local recording
            // app as com.buoyai.buoy_ai.local, so it sits beside BOTH the Play-store app and the
            // staging .film build on a real phone instead of replacing the staging one.
            applicationIdSuffix = when {
                buoyAppSuffix != null -> ".$buoyAppSuffix"
                buoyLocalBuild -> ".local"
                else -> ".film"
            }
            if (buoyAppLabel != null) manifestPlaceholders["appLabel"] = buoyAppLabel
        }
        release {
            signingConfig = if (keystorePropertiesFile.exists()) {
                signingConfigs.getByName("release")
            } else {
                // Fallback to debug signing for local development without key.properties
                signingConfigs.getByName("debug")
            }
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }
}

flutter {
    source = "../.."
}

dependencies {
    // AndroidX Credential Manager provider dependencies for google_sign_in 7.x.
    //
    // On Android, google_sign_in 7.x signs in via the AndroidX Credential Manager.
    // getCredentialAsync needs a registered credential PROVIDER — supplied by
    // credentials-play-services-auth — plus the Google ID token type. The
    // google_sign_in_android plugin declares these transitively, but we pin them
    // as direct app dependencies so the provider is guaranteed on the classpath
    // regardless of plugin resolution. Without the provider (or when R8 strips it —
    // see the keep rules in proguard-rules.pro) sign-in fails at runtime with
    // "GoogleSignInException(code providerConfigurationError, ... no provider
    // dependencies found)". Versions match what google_sign_in_android 7.x ships.
    implementation("androidx.credentials:credentials:1.6.0")
    implementation("androidx.credentials:credentials-play-services-auth:1.6.0")
    implementation("com.google.android.libraries.identity.googleid:googleid:1.2.0")
}

// google-services.json lists the Play-store package and .film only. The app initialises Firebase
// from DefaultFirebaseOptions in Dart, so the Android resources the google-services task generates
// aren't read at runtime; for local recording builds, skip that task instead of failing on the
// unlisted package, and for -PbuoyAppSuffix test builds (the plugin's missingGoogleServicesStrategy only covers a missing file).
if (buoyLocalBuild || buoyAppSuffix != null) {
    tasks.matching { it.name == "processProfileGoogleServices" }.configureEach { enabled = false }
}
