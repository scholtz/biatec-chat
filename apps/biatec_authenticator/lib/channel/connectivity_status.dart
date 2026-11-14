import 'package:connectivity_plus/connectivity_plus.dart';

import 'device_channel.dart';

/// [NetworkStatus] from `connectivity_plus`. A platform that cannot say counts as online: the channel then tries and fails the ordinary way.
class ConnectivityStatus implements NetworkStatus {
  ConnectivityStatus([Connectivity? connectivity])
      : _connectivity = connectivity ?? Connectivity();
  final Connectivity _connectivity;

  static bool _has(List<ConnectivityResult> r) =>
      r.any((c) => c != ConnectivityResult.none);

  @override
  Future<bool> get online async {
    try {
      return _has(await _connectivity.checkConnectivity());
    } on Object {
      return true;
    }
  }

  @override
  Stream<bool> get changes =>
      _connectivity.onConnectivityChanged.map(_has).distinct();
}
