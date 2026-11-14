import java.util.Properties

plugins {
    id("com.android.application")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

android {
    namespace = "io.biatec.authenticator"
    compileSdk = flutter.compileSdkVersion
    ndkVersion = flutter.ndkVersion

    compileOptions {
        // flutter_local_notifications relies on core library desugaring (its README, "Gradle setup").
        isCoreLibraryDesugaringEnabled = true
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        applicationId = "io.biatec.authenticator"
        // You can update the following values to match your application needs.
        // For more information, see: https://flutter.dev/to/review-gradle-config.
        // Android 9 (API 28) at least: it is the first with StrongBox-backed keys, `setUnlockedDeviceRequired`, the signing-certificate API the integrity
        // check reads, and the platform fixes the vault and the biometric lock rely on. A phone older than that is not a safe place for seeds.
        minSdk = maxOf(28, flutter.minSdkVersion)
        targetSdk = flutter.targetSdkVersion
        versionCode = flutter.versionCode
        versionName = flutter.versionName
        // The instrumented test of the device keys (androidTest/.../ApprovalKeyInstrumentedTest.kt).
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        // The Google Cloud project number of the release, for Play Integrity (AttestationChannel). Given with -PplayIntegrityProject=<number> or in
        // gradle.properties; without it the attestation is not available and the enrollment goes without one.
        val integrityProject = (project.findProperty("playIntegrityProject") as String?) ?: "0"
        // A typo here would ship a release that quietly never attests, so a value that is not a number stops the build.
        if (!integrityProject.matches(Regex("\\d+"))) throw GradleException("playIntegrityProject must be the numeric Google Cloud project number")
        manifestPlaceholders["playIntegrityProject"] = integrityProject
    }

    // Release signing material is never in the repository: android/key.properties (git-ignored) names the keystore, see the README, "Release
    // build". Without it the release signing config has no store file and the release build fails; it is never signed with the debug key.
    val keystoreProperties = Properties().apply {
        val file = rootProject.file("key.properties")
        if (file.exists()) file.inputStream().use { load(it) }
    }

    signingConfigs {
        create("release") {
            keyAlias = keystoreProperties.getProperty("keyAlias")
            keyPassword = keystoreProperties.getProperty("keyPassword")
            storeFile = keystoreProperties.getProperty("storeFile")?.let { rootProject.file(it) }
            storePassword = keystoreProperties.getProperty("storePassword")
        }
    }

    buildTypes {
        release {
            // The release defaults, written out so that turning either on is a reviewed change here. They do not override a
            // debuggable flag injected another way; the hardening test checks the merged release manifest for that.
            isDebuggable = false
            isJniDebuggable = false
            signingConfig = signingConfigs.getByName("release")
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

dependencies {
    // Required by flutter_local_notifications (java.time on older Android versions). Its Gson ProGuard rules ship with the plugin from v19.
    coreLibraryDesugaring("com.android.tools:desugar_jdk_libs:2.1.4")
    // The strong-biometric prompt of the two-factor vault (VaultPlugin), also used by the biometric lock (BiometricChannel).
    implementation("androidx.biometric:biometric:1.1.0")
    // Play Integrity, for the platform attestation at enrollment (AttestationChannel).
    implementation("com.google.android.play:integrity:1.4.0")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test:runner:1.6.2")
}

flutter {
    source = "../.."
}
