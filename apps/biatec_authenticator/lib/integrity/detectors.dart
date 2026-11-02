import 'integrity.dart';

/// One kind of evidence. Each detector is a pure function of the [IntegrityEnvironment], so it is tested against a fake file system and fake process information.
abstract class IntegrityDetector {
  const IntegrityDetector();

  /// The group a hit belongs to.
  IntegrityReason get reason;

  /// The platform the detector applies to, or null for both.
  IntegrityPlatform? get platform => null;

  Future<bool> triggered(IntegrityEnvironment env);
}

bool _anyFile(IntegrityEnvironment env, List<String> paths) =>
    paths.any(env.fileExists);

/// Android: files and properties that a rooted phone has.
class AndroidRootDetector extends IntegrityDetector {
  const AndroidRootDetector();

  static const suPaths = [
    '/system/bin/su',
    '/system/xbin/su',
    '/sbin/su',
    '/system/sd/xbin/su',
    '/system/bin/failsafe/su',
    '/data/local/su',
    '/data/local/bin/su',
    '/data/local/xbin/su',
    '/su/bin/su',
    '/vendor/bin/su',
  ];

  static const managerPaths = [
    '/sbin/.magisk',
    '/data/adb/magisk',
    '/data/adb/modules',
    '/cache/magisk.log',
    '/data/adb/ksu',
    '/system/app/Superuser.apk',
    '/system/etc/init.d/99SuperSUDaemon',
  ];

  /// Folders a normal app can never write to; if one can be written, the system partition is open.
  static const protectedFolders = ['/system', '/vendor', '/etc'];

  @override
  IntegrityReason get reason => IntegrityReason.deviceModified;

  @override
  IntegrityPlatform get platform => IntegrityPlatform.android;

  @override
  Future<bool> triggered(IntegrityEnvironment env) async {
    if (_anyFile(env, suPaths) || _anyFile(env, managerPaths)) return true;
    if (protectedFolders.any(env.canWriteTo)) return true;
    final tags = await env.property('ro.build.tags');
    if (tags != null && tags.contains('test-keys')) return true;
    if (await env.property('ro.debuggable') == '1') return true;
    if (await env.property('ro.secure') == '0') return true;
    return false;
  }
}

/// iOS: files and an open sandbox that a jailbroken phone has.
class IosJailbreakDetector extends IntegrityDetector {
  const IosJailbreakDetector();

  static const paths = [
    '/Applications/Cydia.app',
    '/Applications/Sileo.app',
    '/Applications/Zebra.app',
    '/Library/MobileSubstrate/MobileSubstrate.dylib',
    '/Library/MobileSubstrate/DynamicLibraries',
    '/bin/bash',
    '/usr/sbin/sshd',
    '/usr/bin/ssh',
    '/etc/apt',
    '/private/var/lib/apt',
    '/private/var/lib/cydia',
    '/private/var/stash',
    '/var/jb',
    '/usr/libexec/sftp-server',
  ];

  /// Folders the sandbox keeps closed to an app.
  static const protectedFolders = ['/private', '/'];

  @override
  IntegrityReason get reason => IntegrityReason.deviceModified;

  @override
  IntegrityPlatform get platform => IntegrityPlatform.ios;

  @override
  Future<bool> triggered(IntegrityEnvironment env) async =>
      _anyFile(env, paths) || protectedFolders.any(env.canWriteTo);
}

/// A debugger or tracer attached to the process.
class DebuggerDetector extends IntegrityDetector {
  const DebuggerDetector();

  @override
  IntegrityReason get reason => IntegrityReason.toolsAttached;

  @override
  Future<bool> triggered(IntegrityEnvironment env) async {
    if (await env.debuggerAttached()) return true;
    if (env.platform == IntegrityPlatform.android) {
      // A process that is being traced shows the tracer's id in its status.
      final status = env.readFile('/proc/self/status');
      if (status != null) {
        final line =
            RegExp(r'^TracerPid:\s*(\d+)', multiLine: true).firstMatch(status);
        if (line != null && line.group(1) != '0') return true;
      }
    }
    return false;
  }
}

/// Hooking and instrumentation frameworks: Frida, Substrate, Xposed and the like.
class HookingDetector extends IntegrityDetector {
  const HookingDetector();

  static const mapMarkers = [
    // Names of libraries and files, not bare words: a path such as usb_gadget must not match.
    'frida',
    'gum-js-loop',
    'libsubstrate',
    'substrate.so',
    'xposed',
    'lsposed',
    'libriru',
    '/riru',
    'zygisk',
  ];

  static const classNames = [
    'de.robv.android.xposed.XposedBridge',
    'de.robv.android.xposed.XC_MethodHook',
    'com.saurik.substrate.MS',
    'org.lsposed.lspd.core.Main',
  ];

  static const androidFiles = [
    '/data/local/tmp/frida-server',
    '/data/local/tmp/re.frida.server',
    '/data/local/tmp/frida-gadget.so',
  ];

  static const iosImages = [
    'frida',
    'fridagadget',
    'substrate',
    'substitute',
    'libhooker',
    'cycript',
    'sslkillswitch',
    'tweakinject',
  ];

  @override
  IntegrityReason get reason => IntegrityReason.toolsAttached;

  @override
  Future<bool> triggered(IntegrityEnvironment env) async {
    if (env.platform == IntegrityPlatform.android) {
      if (_anyFile(env, androidFiles)) return true;
      final maps = env.readFile('/proc/self/maps')?.toLowerCase();
      if (maps != null && mapMarkers.any(maps.contains)) return true;
      for (final name in classNames) {
        if (await env.classPresent(name)) return true;
      }
      return false;
    }
    for (final image in await env.loadedImages()) {
      final lower = image.toLowerCase();
      if (iosImages.any(lower.contains)) return true;
    }
    return false;
  }
}

/// An emulator or a simulator.
class EmulatorDetector extends IntegrityDetector {
  const EmulatorDetector();

  @override
  IntegrityReason get reason => IntegrityReason.emulator;

  @override
  Future<bool> triggered(IntegrityEnvironment env) async {
    if (env.platform == IntegrityPlatform.ios) return env.isSimulator();
    if (await env.property('ro.kernel.qemu') == '1') return true;
    final hardware = (await env.property('ro.hardware'))?.toLowerCase() ?? '';
    if (hardware.contains('goldfish') ||
        hardware.contains('ranchu') ||
        hardware.contains('vbox')) {
      return true;
    }
    final fingerprint =
        (await env.property('ro.build.fingerprint'))?.toLowerCase() ?? '';
    if (fingerprint.startsWith('generic') || fingerprint.contains('emulator')) {
      return true;
    }
    final model = (await env.property('ro.product.model'))?.toLowerCase() ?? '';
    return model.contains('emulator') ||
        model.contains('android sdk built for');
  }
}

/// Android: the app must be signed with the release certificate. A copy that was repackaged and signed by someone else has another digest. The expected digest comes from the build's configuration; with none configured the app is not trusted either (a release build without it would be unverifiable).
class SigningCertificateDetector extends IntegrityDetector {
  const SigningCertificateDetector(this.expectedDigest);

  /// SHA-256 of the release certificate, in any case with or without colons, or empty when not configured.
  final String expectedDigest;

  /// Lower-case hex without separators.
  static String normalize(String digest) =>
      digest.replaceAll(RegExp(r'[^0-9a-fA-F]'), '').toLowerCase();

  @override
  IntegrityReason get reason => IntegrityReason.appNotGenuine;

  @override
  IntegrityPlatform get platform => IntegrityPlatform.android;

  @override
  Future<bool> triggered(IntegrityEnvironment env) async {
    final expected = normalize(expectedDigest);
    if (expected.isEmpty) return true;
    final actual = await env.signingDigest();
    return actual == null || normalize(actual) != expected;
  }
}
