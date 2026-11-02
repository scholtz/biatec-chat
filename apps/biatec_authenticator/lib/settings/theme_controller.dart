import 'package:flutter/material.dart';

import '../security/secure_storage.dart';

/// Where the theme choice is kept between runs.
abstract class ThemePreferenceStore {
  Future<String?> read();
  Future<void> write(String value);
}

/// On this phone only, in the same secure storage as the other local settings.
class SecureThemePreferenceStore implements ThemePreferenceStore {
  const SecureThemePreferenceStore();

  static const _key = 'biatec.theme';

  @override
  Future<String?> read() => deviceBoundStorage.read(key: _key);

  @override
  Future<void> write(String value) =>
      deviceBoundStorage.write(key: _key, value: value);
}

/// The user's choice of system, light or dark appearance. A choice applies at once, then is saved; a saved choice is loaded at start, and a value that is not recognised (or storage that cannot be read) leaves it on the system setting.
class ThemeController extends ChangeNotifier {
  ThemeController({ThemePreferenceStore? store})
      : _store = store ?? const SecureThemePreferenceStore();

  final ThemePreferenceStore _store;
  ThemeMode _mode = ThemeMode.system;
  bool _chosen = false;

  ThemeMode get mode => _mode;

  /// Reads the saved choice. A choice made before the read finished wins over the saved one.
  Future<void> load() async {
    try {
      final saved = await _store.read();
      if (_chosen) return;
      for (final m in ThemeMode.values) {
        if (m.name == saved) {
          _mode = m;
          notifyListeners();
          return;
        }
      }
    } on Object {
      // Storage that cannot be read leaves the system setting.
    }
  }

  /// Applies [mode] at once and saves it. A save that fails still leaves the choice in force for this run.
  Future<void> select(ThemeMode mode) async {
    _chosen = true;
    if (_mode != mode) {
      _mode = mode;
      notifyListeners();
    }
    try {
      await _store.write(mode.name);
    } on Object {
      // Not saved: it applies until the app closes.
    }
  }
}

/// Makes the [ThemeController] available to the Settings screen.
class ThemeScope extends InheritedNotifier<ThemeController> {
  const ThemeScope(
      {super.key, required ThemeController controller, required super.child})
      : super(notifier: controller);

  /// Null when the app has no theme choice (a screen shown on its own in a test).
  static ThemeController? maybeOf(BuildContext context) =>
      context.dependOnInheritedWidgetOfExactType<ThemeScope>()?.notifier;
}
