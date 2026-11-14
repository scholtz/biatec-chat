import 'dart:typed_data';

import '../enrollment/enrollment_state.dart';
import 'device_key_store.dart';

/// Which keystore key signs what needs no biometric prompt for an enrollment [record]: the ONE place that decides (device proofs, deny and `code_failed`
/// receipts, and every signature of single-key mode use it).
///
///  * A two-key record (`presence_kid`): the presence key, under `presence_kid`.
///  * A single-key record made by this build (`kid` starts with `biatec-presence-`): the presence key, under `kid`.
///  * Any other single-key record: an install from BEFORE the two keys, whose key lives under the old random alias that is also the record's `kid`. The
///    native plugins still find and sign with it (`loadLegacy` / `signLegacy`). It stays valid in single-key mode; in two-key mode it is not (the app
///    asks to enroll again, see `EnrollmentController`).
class PromptFreeKey {
  const PromptFreeKey._(this.kid, this.legacy);

  factory PromptFreeKey.of(EnrollmentRecord record) {
    final presence = record.presenceKid;
    if (presence != null) return PromptFreeKey._(presence, false);
    return PromptFreeKey.forSingleKid(record.kid);
  }

  /// The key of a single-key record named [kid].
  factory PromptFreeKey.forSingleKid(String kid) =>
      PromptFreeKey._(kid, !kid.startsWith(presenceKidPrefix));

  /// The kid the key has on the server (the header `kid` of a device proof).
  final String kid;

  /// The key lives under an old alias (an install from before the two keys).
  final bool legacy;

  /// Where this build's presence kids start (see [DeviceKey.fromParts]).
  static const presenceKidPrefix = 'biatec-presence-';

  Future<DeviceKey?> load(DeviceKeyStore keys) async {
    final key = legacy ? await keys.loadLegacy(kid) : await keys.loadPresence();
    return key != null && key.kid == kid ? key : null;
  }

  Future<Uint8List> sign(DeviceKeyStore keys, Uint8List data) =>
      legacy ? keys.signLegacy(kid, data) : keys.signPresence(data);
}

/// Removes every device key of this install: the new aliases (`deleteKeys`) AND, for a single-key record from before the two keys, the key under its old alias
/// (`deleteKeys` does not touch that one). Used when the device signs out, is cancelled or enrolls again. Each step is best effort.
Future<void> deleteDeviceKeys(
    DeviceKeyStore keys, EnrollmentRecord? record) async {
  try {
    await keys.deleteKeys();
  } on Object {
    // Best effort.
  }
  if (record == null) return;
  final key = PromptFreeKey.of(record);
  if (!key.legacy) return;
  try {
    await keys.deleteLegacyKey(key.kid);
  } on Object {
    // Best effort.
  }
}
