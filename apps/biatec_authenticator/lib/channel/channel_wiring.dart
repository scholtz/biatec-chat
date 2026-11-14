import 'dart:async';

import 'package:flutter/foundation.dart' show Listenable;

import '../approvals/approval_client.dart';
import '../config.dart';
import '../enrollment/enrollment_store.dart';
import '../push/device_event.dart';
import '../push/device_event_source.dart';
import 'connectivity_status.dart';
import 'device_channel.dart';

/// The device channel of this app, built the same way in the app and in the Android service: one ticket per connection, asked for with the device token and the device proof that [client] adds to every call. There is no other way to a ticket, so a channel without the device key is not possible.
///
/// [wake] tells a waiting channel to look again (the enrollment changed). [onNotEnrolled] runs when the enrollment is read and found missing (the Android service stops itself then).
DeviceChannel buildDeviceChannel({
  required AppConfig config,
  required ApprovalClient client,
  required EnrollmentStore enrollment,
  Stream<void>? wake,
  NetworkStatus? network,
  FutureOr<void> Function()? onNotEnrolled,
  FutureOr<void> Function(int status)? onRefused,
}) =>
    DeviceChannel(
      hubUri: DeviceChannel.hubUriFor(config.oidcBaseUrl),
      network: network ?? ConnectivityStatus(),
      wake: wake,
      enrolled: () async {
        final record = await enrollment.read();
        if (record == null) await onNotEnrolled?.call();
        return record != null;
      },
      ticket: () async {
        final record = await enrollment.read();
        if (record == null) throw StateError('not enrolled');
        try {
          return (await client.hubTicket(record.deviceToken)).ticket;
        } on ApprovalException catch (e) {
          // 401 or 403: the device was revoked or cancelled (or, for 401, the phone's clock is off). The channel stops and tries again only much later.
          final status = e.status;
          if (status == 401 || status == 403) {
            await onRefused?.call(status!);
            throw ChannelRefused(status);
          }
          rethrow;
        }
      },
    );

/// A stream that fires each time [source] notifies (the observable enrollment store), for [buildDeviceChannel]'s `wake`.
Stream<void> changesOf(Listenable source) {
  late final StreamController<void> controller;
  void fire() => controller.add(null);
  controller = StreamController<void>.broadcast(
    onListen: () => source.addListener(fire),
    onCancel: () => source.removeListener(fire),
  );
  return controller.stream;
}

/// The source of a build whose Biatec OIDC has no device channel: it never connects and never asks for a ticket.
class IdleEventSource implements DeviceEventSource {
  @override
  Stream<DeviceEvent> get events => const Stream.empty();
  @override
  Future<void> start() async {}
  @override
  Future<void> stop() async {}
}
