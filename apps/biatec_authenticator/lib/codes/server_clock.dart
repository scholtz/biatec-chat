import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;

/// How far this phone's clock is from Biatec OIDC's, learned from the `Date` header of the calls the app makes anyway (no extra request).
///
/// Time-based codes depend on the clock, so a phone that is off by more than [limit] gets a warning that explains the fix.
class ServerClock extends ChangeNotifier {
  ServerClock({DateTime Function()? clock}) : _now = clock ?? DateTime.now;

  /// The clock the whole app reports to; the pinned HTTP client feeds it.
  static final ServerClock instance = ServerClock();

  final DateTime Function() _now;

  /// The most the phone may be off before the user is told. A little is network delay and the header's one-second resolution.
  static const limit = Duration(seconds: 5);

  Duration? _offset;

  /// Server time minus phone time, or null before the first answer. Positive: the phone is behind.
  Duration? get offset => _offset;

  /// True once a call showed the phone more than [limit] away.
  bool get isOff => (_offset?.abs() ?? Duration.zero) > limit;

  /// Reads the `Date` header of an answer to a request sent at [sentAt] and received at [receivedAt]. A missing or unreadable header is ignored.
  void observe(String? dateHeader, DateTime sentAt, DateTime receivedAt) {
    if (dateHeader == null) return;
    final DateTime server;
    try {
      server = HttpDate.parse(dateHeader);
    } on Object {
      return;
    }
    // The header has whole seconds: the server was between its value and one second later. The answer was made around the middle of the call.
    final middle = sentAt.add(receivedAt.difference(sentAt) ~/ 2);
    final next =
        server.add(const Duration(milliseconds: 500)).difference(middle);
    if (_offset != null &&
        (_offset! - next).abs() < const Duration(seconds: 1)) {
      _offset = next;
      return;
    }
    _offset = next;
    notifyListeners();
  }

  /// Forgets what was learned (a sign-out).
  void reset() {
    if (_offset == null) return;
    _offset = null;
    notifyListeners();
  }

  /// A client that reports the `Date` header of every answer to [report].
  http.Client wrap(http.Client inner) => DateObservingClient(inner, this);

  DateTime get now => _now();
}

/// Passes every call through and tells the [ServerClock] when it was sent, received and what the server's `Date` was.
class DateObservingClient extends http.BaseClient {
  DateObservingClient(this._inner, this._clock);

  final http.Client _inner;
  final ServerClock _clock;

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    final sent = _clock.now;
    final response = await _inner.send(request);
    _clock.observe(response.headers['date'], sent, _clock.now);
    return response;
  }

  @override
  void close() => _inner.close();
}
