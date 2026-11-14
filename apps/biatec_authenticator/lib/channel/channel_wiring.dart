import 'dart:async';

import 'package:flutter/foundation.dart' show Listenable;

import '../approvals/approval_client.dart';
import '../config.dart';
import '../enrollment/enrollment_store.dart';
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
        return (await client.hubTicket(record.deviceToken)).ticket;
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
