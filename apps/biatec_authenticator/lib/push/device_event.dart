/// What the device channel can tell the app. Every event carries an id at most, never content: the app fetches what it shows from Biatec OIDC over its own authenticated call.
sealed class DeviceEvent {
  const DeviceEvent();
}

/// `consent_request { auth_req_id }`: a request is waiting for this device.
class ConsentRequested extends DeviceEvent {
  const ConsentRequested(this.authReqId);
  final String authReqId;

  @override
  bool operator ==(Object other) =>
      other is ConsentRequested && other.authReqId == authReqId;
  @override
  int get hashCode => Object.hash('requested', authReqId);
}

/// `consent_closed { auth_req_id }`: the request was decided, expired or withdrawn.
class ConsentClosed extends DeviceEvent {
  const ConsentClosed(this.authReqId);
  final String authReqId;

  @override
  bool operator ==(Object other) =>
      other is ConsentClosed && other.authReqId == authReqId;
  @override
  int get hashCode => Object.hash('closed', authReqId);
}

/// `device_enrolled { kid }`: another device was added to this user.
class DeviceEnrolled extends DeviceEvent {
  const DeviceEnrolled(this.kid);
  final String kid;

  @override
  bool operator ==(Object other) => other is DeviceEnrolled && other.kid == kid;
  @override
  int get hashCode => Object.hash('enrolled', kid);
}

/// `settings_changed {}`: the consent settings changed on another device.
class SettingsChanged extends DeviceEvent {
  const SettingsChanged();

  @override
  bool operator ==(Object other) => other is SettingsChanged;
  @override
  int get hashCode => 'settings'.hashCode;
}

/// Ids are plain tokens: letters, digits and `_ . -`, 1 to 128 of them. Anything else is not an id this app made or was sent by Biatec OIDC.
final _id = RegExp(r'^[A-Za-z0-9_.\-]{1,128}$');

/// [value] when it is a plausible id, else null.
String? validId(Object? value) =>
    value is String && _id.hasMatch(value) ? value : null;

/// The `auth_req_id` of a payload, or null when it is missing or not a plausible id. Titles, bodies and other keys are never read.
String? authReqIdFromPayload(Object? payload) =>
    payload is Map ? validId(payload['auth_req_id']) : null;

/// The event for a hub invocation, or null for an unknown target or an argument that does not validate (such an invocation is ignored, never half used).
DeviceEvent? parseDeviceEvent(String target, List<Object?> arguments) {
  final payload = arguments.isEmpty ? null : arguments.first;
  switch (target) {
    case 'consent_request':
      final id = authReqIdFromPayload(payload);
      return id == null ? null : ConsentRequested(id);
    case 'consent_closed':
      final id = authReqIdFromPayload(payload);
      return id == null ? null : ConsentClosed(id);
    case 'device_enrolled':
      final kid = payload is Map ? validId(payload['kid']) : null;
      return kid == null ? null : DeviceEnrolled(kid);
    case 'settings_changed':
      return const SettingsChanged();
  }
  return null;
}
