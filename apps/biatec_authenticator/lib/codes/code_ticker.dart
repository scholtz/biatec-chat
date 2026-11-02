import 'dart:async';

import 'package:flutter/foundation.dart';

/// One clock for every countdown on the Codes tab. It ticks on the whole second of the clock it is given (it asks the clock each time and aims at the next second, so it does not drift), and only while it is running: the tab is showing, the app is in front and unlocked.
class CodeTicker extends ValueNotifier<DateTime> {
  CodeTicker({required DateTime Function() clock})
      : _clock = clock,
        super(clock());

  final DateTime Function() _clock;
  Timer? _timer;

  bool get running => _timer != null;

  /// Starts ticking. The value is brought up to date at once.
  void start() {
    if (running) return;
    value = _clock();
    _schedule();
  }

  /// Stops. Nothing is scheduled afterwards.
  void stop() {
    _timer?.cancel();
    _timer = null;
  }

  void _schedule() {
    final now = _clock();
    // To just after the next whole second, so the second shown is the new one.
    final wait = Duration(milliseconds: 1000 - now.millisecond + 10);
    _timer = Timer(wait, () {
      value = _clock();
      _schedule();
    });
  }

  @override
  void dispose() {
    stop();
    super.dispose();
  }
}
