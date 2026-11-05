import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;

/// This build's number: the part after the plus sign of `version` in pubspec.yaml. The build passes it with `--dart-define=APP_BUILD=<n>` (Flutter does not give it to Dart code without a plugin); a test keeps the default equal to pubspec.yaml.
const int appBuild = int.fromEnvironment('APP_BUILD', defaultValue: 1);

/// An optional date after which this build stops opening, `--dart-define=BUILD_EXPIRES=2027-06-30`. The fallback while Biatec OIDC does not send a minimum build (see [MinimumBuildMonitor]): a build that nobody updates cannot run for ever. Empty means it does not expire.
const String buildExpires = String.fromEnvironment('BUILD_EXPIRES');

/// The lowest build number Biatec OIDC accepts, learned from the `X-Biatec-Min-App-Build` header of the calls the app makes anyway (no extra request). Only a higher number is kept: a later answer, or a man in the middle, cannot lower it again until the app restarts.
class MinimumBuildMonitor extends ChangeNotifier {
  MinimumBuildMonitor();

  /// The one the whole app reports to; the pinned HTTP client feeds it.
  static final MinimumBuildMonitor instance = MinimumBuildMonitor();

  static const header = 'x-biatec-min-app-build';

  int _required = 0;

  /// The highest minimum build seen, or 0 before any answer carried one.
  int get required => _required;

  /// Reads the header value of an answer. Something that is not a whole number from 0 to a million is ignored.
  void observe(String? value) {
    final n = int.tryParse(value?.trim() ?? '');
    if (n == null || n < 0 || n > 1000000 || n <= _required) return;
    _required = n;
    notifyListeners();
  }

  /// A client that reports every answer's header to this monitor.
  http.Client wrap(http.Client inner) => _MinimumBuildClient(inner, this);
}

class _MinimumBuildClient extends http.BaseClient {
  _MinimumBuildClient(this._inner, this._monitor);

  final http.Client _inner;
  final MinimumBuildMonitor _monitor;

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    final response = await _inner.send(request);
    _monitor.observe(response.headers[MinimumBuildMonitor.header]);
    return response;
  }

  @override
  void close() => _inner.close();
}

/// Why the secure area stays closed for want of an update.
enum UpdateReason { tooOld, expired }

/// Decides whether this build may still open: it is not older than the minimum Biatec OIDC asks for, and it has not passed its expiry date.
class UpdateGuard {
  UpdateGuard({
    required this.monitor,
    this.build = appBuild,
    String expires = buildExpires,
    DateTime Function()? clock,
  })  : expiresOn = _utc(DateTime.tryParse(expires)),
        _clock = clock ?? DateTime.now;

  /// A date or date and time given without a zone is read as UTC, so every phone ends the build at the same moment.
  static DateTime? _utc(DateTime? d) => d == null || d.isUtc
      ? d
      : DateTime.utc(d.year, d.month, d.day, d.hour, d.minute, d.second);

  final MinimumBuildMonitor monitor;
  final int build;

  /// The expiry date, or null when the build does not expire (or the text was not a date: a mistake in the build settings does not close every install).
  final DateTime? expiresOn;
  final DateTime Function() _clock;

  /// Null while the build may open.
  UpdateReason? get blocked {
    if (monitor.required > build) return UpdateReason.tooOld;
    final end = expiresOn;
    if (end != null && !_clock().toUtc().isBefore(end.toUtc())) {
      return UpdateReason.expired;
    }
    return null;
  }
}
