import 'package:flutter/cupertino.dart' show CupertinoPageTransitionsBuilder;
import 'package:flutter/material.dart';

/// The one answer to "may anything move?". The system's reduce-motion setting (Android "remove animations", iOS "reduce motion") reaches Flutter as `disableAnimations`; every animation and every haptic in the app asks here and does nothing when it is on.
abstract final class Motion {
  static bool reduced(BuildContext context) =>
      MediaQuery.disableAnimationsOf(context);

  /// [normal] unless motion is reduced, then zero.
  static Duration duration(BuildContext context, Duration normal) =>
      reduced(context) ? Duration.zero : normal;

  static const quick = Duration(milliseconds: 180);
  static const page = Duration(milliseconds: 260);
  static const ring = Duration(milliseconds: 900);
}

/// Page transitions of every route: a short fade with a small rise, and no movement at all under reduce-motion.
class QuietPageTransitions extends PageTransitionsBuilder {
  const QuietPageTransitions();

  @override
  Widget buildTransitions<T>(
    PageRoute<T> route,
    BuildContext context,
    Animation<double> animation,
    Animation<double> secondaryAnimation,
    Widget child,
  ) {
    if (Motion.reduced(context)) return child;
    final curved =
        CurvedAnimation(parent: animation, curve: Curves.easeOutCubic);
    return FadeTransition(
      opacity: curved,
      child: SlideTransition(
        position: Tween<Offset>(begin: const Offset(0, 0.03), end: Offset.zero)
            .animate(curved),
        child: child,
      ),
    );
  }
}

/// Android, Windows and Linux use [QuietPageTransitions]; Apple platforms keep their own.
const quietPageTransitions = PageTransitionsTheme(builders: {
  TargetPlatform.android: QuietPageTransitions(),
  // iOS and macOS keep the native slide and the swipe back from the edge.
  TargetPlatform.iOS: CupertinoPageTransitionsBuilder(),
  TargetPlatform.macOS: CupertinoPageTransitionsBuilder(),
  TargetPlatform.windows: QuietPageTransitions(),
  TargetPlatform.linux: QuietPageTransitions(),
  TargetPlatform.fuchsia: QuietPageTransitions(),
});
