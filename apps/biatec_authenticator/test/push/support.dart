import 'dart:async';

import 'package:biatec_authenticator/push/device_event.dart';
import 'package:biatec_authenticator/push/device_event_controller.dart';
import 'package:biatec_authenticator/push/device_event_source.dart';

/// A [DeviceEventSource] the test drives by hand.
class FakeSource implements DeviceEventSource {
  final controller = StreamController<DeviceEvent>.broadcast();
  int starts = 0;
  int stops = 0;

  @override
  Stream<DeviceEvent> get events => controller.stream;
  @override
  Future<void> start() async => starts++;
  @override
  Future<void> stop() async => stops++;
}

/// Records what the app would show, by id only: the notifier has no way to take text from an event.
class FakeNotifier implements DeviceNotifier {
  final requests = <String>[];
  final cancelled = <String>[];
  final devices = <String>[];

  @override
  Future<void> requestArrived(String authReqId) async =>
      requests.add(authReqId);
  @override
  Future<void> deviceEnrolled(String kid) async => devices.add(kid);
  @override
  Future<void> cancelRequest(String authReqId) async =>
      cancelled.add(authReqId);
}

class FakeApple implements ApplePushRegistration {
  final refreshes = StreamController<String>.broadcast();
  String? current = 'apns-1';

  @override
  Future<String?> token() async => current;
  @override
  Stream<String> get tokenRefreshes => refreshes.stream;
}
