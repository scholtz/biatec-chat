import 'package:flutter/material.dart';

/// Material 3 theme. Type size follows the system scale (see largeTypeBuilder).
ThemeData buildTheme(Brightness brightness) {
  final base = ThemeData(
    useMaterial3: true,
    brightness: brightness,
    colorSchemeSeed: const Color(0xFF1B5E8C),
  );
  return base.copyWith(
    listTileTheme: const ListTileThemeData(minVerticalPadding: 12),
  );
}

/// Honors the system font scale up to 3x so large-type users are supported
/// without breaking layouts.
Widget largeTypeBuilder(BuildContext context, Widget? child) {
  final media = MediaQuery.of(context);
  return MediaQuery(
    data: media.copyWith(
      textScaler: media.textScaler.clamp(
        maxScaleFactor: 3.0,
      ),
    ),
    child: child ?? const SizedBox.shrink(),
  );
}
