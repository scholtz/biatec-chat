import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'motion.dart';

/// The moments the app answers with a touch.
enum HapticKind { copy, approve, deny, error }

/// Plays [kind] on the device. Replaced in tests.
typedef HapticSink = Future<void> Function(HapticKind kind);

Future<void> _platformSink(HapticKind kind) => switch (kind) {
      HapticKind.copy => HapticFeedback.selectionClick(),
      HapticKind.approve => HapticFeedback.mediumImpact(),
      HapticKind.deny => HapticFeedback.lightImpact(),
      HapticKind.error => HapticFeedback.heavyImpact(),
    };

/// The only place that triggers haptic feedback. It does nothing when the system asks for reduced motion (the platform's own haptics setting still applies to what it plays), and it never throws: a missing vibration motor is not an error.
class Haptics {
  Haptics({HapticSink? sink}) : _sink = sink ?? _platformSink;

  /// The helper the screens use; tests replace it.
  static Haptics instance = Haptics();

  final HapticSink _sink;

  Future<void> copy(BuildContext context) => _play(context, HapticKind.copy);
  Future<void> approve(BuildContext context) =>
      _play(context, HapticKind.approve);
  Future<void> deny(BuildContext context) => _play(context, HapticKind.deny);
  Future<void> error(BuildContext context) => _play(context, HapticKind.error);

  Future<void> _play(BuildContext context, HapticKind kind) async {
    if (Motion.reduced(context)) return;
    try {
      await _sink(kind);
    } on Object {
      // No haptics on this device.
    }
  }
}
