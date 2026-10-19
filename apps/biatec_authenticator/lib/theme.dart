import 'package:flutter/material.dart';

/// Material 3 theme with larger base type and roomy touch targets.
ThemeData buildTheme(Brightness brightness) {
  final base = ThemeData(
    useMaterial3: true,
    brightness: brightness,
    colorSchemeSeed: const Color(0xFF1B5E8C),
  );
  return base.copyWith(
    textTheme: base.textTheme.apply(fontSizeFactor: 1.1),
    listTileTheme: const ListTileThemeData(minVerticalPadding: 12),
  );
}

/// Honors the system font scale up to 2x so large-type users are supported
/// without breaking layouts.
Widget largeTypeBuilder(BuildContext context, Widget? child) {
  final media = MediaQuery.of(context);
  return MediaQuery(
    data: media.copyWith(
      textScaler: media.textScaler.clamp(
        minScaleFactor: 1.0,
        maxScaleFactor: 2.0,
      ),
    ),
    child: child ?? const SizedBox.shrink(),
  );
}
