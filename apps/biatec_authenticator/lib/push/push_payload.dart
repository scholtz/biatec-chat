/// The only thing a push may carry is the request id (protocol: "the
/// notification carries only a request identifier; all content is fetched
/// from Biatec OIDC"). Anything else in the payload is ignored.
final _authReqId = RegExp(r'^[A-Za-z0-9_.\-]{1,128}$');

/// The `auth_req_id` of a push data payload, or null when it is missing or
/// not a plausible id. Titles, bodies and other keys are never read.
String? authReqIdFromPush(Map<String, Object?> data) {
  final id = data['auth_req_id'];
  return id is String && _authReqId.hasMatch(id) ? id : null;
}
