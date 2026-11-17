import '../enrollment/enrollment_state.dart';
import '../keys/device_key_store.dart';
import '../keys/key_security_level.dart';
import 'approval_client.dart';
import 'approval_texts.dart';
import 'receipt.dart';

/// The two keys of an enrolled device (protocol 1.1) with the attestation Biatec OIDC issued for each: what signs a receipt and a link statement. The caller has checked that the device is enrolled and
/// active. Throws [ApprovalException] when the keys cannot sign: an install from before the two keys, a missing presence key, or a key that sits in software. The approval key is null in the identity
/// when it is invalidated or gone: such a device can still deny.
Future<DeviceIdentity> twoKeyIdentityOf(
    EnrollmentRecord record, DeviceKeyStore keys) async {
  final presenceKid = record.presenceKid;
  final presenceAttestation = record.presenceAttestation;
  if (presenceKid == null || presenceAttestation == null) {
    // An install from before the two keys: its one key is not bound to the biometrics. No silent migration.
    throw ApprovalException(ApprovalTexts.of().text('oldKey'));
  }
  final presence = await keys.loadPresence();
  if (presence == null || presence.kid != presenceKid) {
    throw ApprovalException(ApprovalTexts.of().text('keyMissing'));
  }
  if (presence.securityLevel.blocksSigning) {
    throw ApprovalException(KeySecurityLevel.softwareKeyMessage);
  }
  final approval = await keys.loadApproval();
  return DeviceIdentity(
    approvalKey:
        approval != null && approval.kid == record.kid ? approval : null,
    approvalAttestation: record.attestation,
    presenceKey: presence,
    presenceAttestation: presenceAttestation,
  );
}

/// Why this device cannot approve with its approval key, or null when it can: it must be in hardware, bound to the biometrics, and not invalidated. [state] is what the keystore said at the last check
/// (null: it did not answer, which does not turn approving off: signing then fails by itself).
String? cannotApproveWith(DeviceIdentity device, KeysState? state) {
  if (state == KeysState.missing) return ApprovalKeysMissing.message;
  if (state == KeysState.approvalKeyInvalidated) {
    return ApprovalKeyInvalidated.message;
  }
  final key = device.approvalKey;
  // The state says ok but the key cannot be loaded: it is gone.
  if (key == null) return ApprovalKeysMissing.message;
  if (device.approvalAttestation == null) {
    return ApprovalTexts.of().text('noAttestation');
  }
  if (key.securityLevel.blocksSigning || !key.gestureRequired) {
    return ApprovalTexts.of().text('cannotProtect');
  }
  return null;
}
