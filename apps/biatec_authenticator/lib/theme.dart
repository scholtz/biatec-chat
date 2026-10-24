import 'package:flutter/material.dart';

/// Spacing scale (dp). Use these instead of literals so screens line up.
abstract final class AppSpacing {
  static const double xs = 4;
  static const double s = 8;
  static const double m = 16;
  static const double l = 24;
  static const double xl = 32;
}

/// Corner radii (dp).
abstract final class AppRadius {
  static const double s = 8;
  static const double m = 12;
  static const double l = 16;
  static const double xl = 28;
}

/// Smallest tap target (dp), per Material and WCAG 2.5.8 guidance.
const double kMinTapTarget = 48;

/// Widest column of content on large screens (dp); phones are unaffected.
const double kMaxContentWidth = 640;

/// Colors for status messages. Always shown with an icon and words, never as the only signal.
@immutable
class StatusColors extends ThemeExtension<StatusColors> {
  const StatusColors({
    required this.info,
    required this.onInfo,
    required this.success,
    required this.onSuccess,
    required this.warning,
    required this.onWarning,
    required this.danger,
    required this.onDanger,
  });

  final Color info;
  final Color onInfo;
  final Color success;
  final Color onSuccess;
  final Color warning;
  final Color onWarning;
  final Color danger;
  final Color onDanger;

  static const light = StatusColors(
    info: Color(0xFFD7E8F5),
    onInfo: Color(0xFF0B2F49),
    success: Color(0xFFD5EFDC),
    onSuccess: Color(0xFF0C3A1A),
    warning: Color(0xFFFFE9B8),
    onWarning: Color(0xFF4A3200),
    danger: Color(0xFFFAD4D1),
    onDanger: Color(0xFF5C0A07),
  );

  static const dark = StatusColors(
    info: Color(0xFF16384F),
    onInfo: Color(0xFFCDE5F7),
    success: Color(0xFF164023),
    onSuccess: Color(0xFFCBEFD3),
    warning: Color(0xFF4D3A0B),
    onWarning: Color(0xFFFFE2A3),
    danger: Color(0xFF5A1A17),
    onDanger: Color(0xFFFFD9D5),
  );

  @override
  StatusColors copyWith({
    Color? info,
    Color? onInfo,
    Color? success,
    Color? onSuccess,
    Color? warning,
    Color? onWarning,
    Color? danger,
    Color? onDanger,
  }) =>
      StatusColors(
        info: info ?? this.info,
        onInfo: onInfo ?? this.onInfo,
        success: success ?? this.success,
        onSuccess: onSuccess ?? this.onSuccess,
        warning: warning ?? this.warning,
        onWarning: onWarning ?? this.onWarning,
        danger: danger ?? this.danger,
        onDanger: onDanger ?? this.onDanger,
      );

  @override
  StatusColors lerp(ThemeExtension<StatusColors>? other, double t) {
    if (other is! StatusColors) return this;
    return StatusColors(
      info: Color.lerp(info, other.info, t)!,
      onInfo: Color.lerp(onInfo, other.onInfo, t)!,
      success: Color.lerp(success, other.success, t)!,
      onSuccess: Color.lerp(onSuccess, other.onSuccess, t)!,
      warning: Color.lerp(warning, other.warning, t)!,
      onWarning: Color.lerp(onWarning, other.onWarning, t)!,
      danger: Color.lerp(danger, other.danger, t)!,
      onDanger: Color.lerp(onDanger, other.onDanger, t)!,
    );
  }
}

extension AppThemeContext on BuildContext {
  StatusColors get statusColors {
    final theme = Theme.of(this);
    return theme.extension<StatusColors>() ??
        (theme.brightness == Brightness.dark
            ? StatusColors.dark
            : StatusColors.light);
  }
}

/// Material 3 theme. Type size follows the system scale (see largeTypeBuilder).
ThemeData buildTheme(Brightness brightness) {
  final scheme = ColorScheme.fromSeed(
    seedColor: const Color(0xFF1B5E8C),
    brightness: brightness,
  );
  final base = ThemeData(
    useMaterial3: true,
    brightness: brightness,
    colorScheme: scheme,
  );
  final text = base.textTheme;
  final shape =
      RoundedRectangleBorder(borderRadius: BorderRadius.circular(AppRadius.m));
  return base.copyWith(
    scaffoldBackgroundColor: scheme.surface,
    visualDensity: VisualDensity.standard,
    materialTapTargetSize: MaterialTapTargetSize.padded,
    textTheme: text.copyWith(
      headlineSmall: text.headlineSmall?.copyWith(fontWeight: FontWeight.w600),
      titleLarge: text.titleLarge?.copyWith(fontWeight: FontWeight.w600),
      titleMedium: text.titleMedium?.copyWith(fontWeight: FontWeight.w600),
    ),
    extensions: [
      brightness == Brightness.dark ? StatusColors.dark : StatusColors.light,
    ],
    appBarTheme: AppBarTheme(
      centerTitle: false,
      scrolledUnderElevation: 2,
      backgroundColor: scheme.surface,
      foregroundColor: scheme.onSurface,
      titleTextStyle: text.titleLarge
          ?.copyWith(fontWeight: FontWeight.w600, color: scheme.onSurface),
    ),
    cardTheme: CardThemeData(
      elevation: 0,
      margin: EdgeInsets.zero,
      color: scheme.surfaceContainerLow,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(AppRadius.l),
        side: BorderSide(color: scheme.outlineVariant),
      ),
    ),
    navigationBarTheme: NavigationBarThemeData(
      height: 72,
      labelBehavior: NavigationDestinationLabelBehavior.alwaysShow,
      backgroundColor: scheme.surfaceContainer,
      indicatorColor: scheme.secondaryContainer,
      labelTextStyle: WidgetStateProperty.resolveWith((states) {
        final selected = states.contains(WidgetState.selected);
        return text.labelMedium?.copyWith(
          fontWeight: selected ? FontWeight.w700 : FontWeight.w500,
          color: selected ? scheme.onSurface : scheme.onSurfaceVariant,
        );
      }),
    ),
    navigationRailTheme: NavigationRailThemeData(
      backgroundColor: scheme.surfaceContainer,
      indicatorColor: scheme.secondaryContainer,
      labelType: NavigationRailLabelType.all,
    ),
    listTileTheme: const ListTileThemeData(minVerticalPadding: 12),
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        minimumSize: const Size(kMinTapTarget, kMinTapTarget),
        shape: shape,
      ),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: OutlinedButton.styleFrom(
        minimumSize: const Size(kMinTapTarget, kMinTapTarget),
        shape: shape,
      ),
    ),
    inputDecorationTheme: InputDecorationTheme(
      border:
          OutlineInputBorder(borderRadius: BorderRadius.circular(AppRadius.m)),
    ),
    snackBarTheme: SnackBarThemeData(
      behavior: SnackBarBehavior.floating,
      shape: shape,
    ),
    dividerTheme: DividerThemeData(color: scheme.outlineVariant, space: 1),
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
