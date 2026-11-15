import '../../text/text_rules.dart';
import '../consent_request.dart';

/// What the device fetches with `GET /consent/links/{id}` (`schemas/link-request.schema.json`): the service that wants to be linked, as Biatec OIDC knows it, the label the service
/// gave this user, a nonce and the time the link runs out. Parsed strictly: anything the screen could not show faithfully throws [FormatException] and the link is never offered.
class LinkRequest {
  const LinkRequest({
    required this.linkId,
    required this.client,
    required this.nonce,
    required this.expiresAt,
    this.userLabel,
  });

  final String linkId;

  /// `{ fingerprint, domain_proven, domain? }`. Only a proven domain says who it is; nothing here is a name.
  final ConsentClient client;

  /// What the service calls this user (for example a masked name), as the service wrote it: it passed the relying-party text rules (at most 40 code points), and is always drawn as written by the
  /// service. Null when the service gave none.
  final String? userLabel;
  final String nonce;

  /// Unix seconds.
  final int expiresAt;

  DateTime get expiresAtTime =>
      DateTime.fromMillisecondsSinceEpoch(expiresAt * 1000, isUtc: true);

  bool isExpired(DateTime now) => !expiresAtTime.isAfter(now);

  /// The domain the statement signs: the proven one, or null for a service that has not proven its domain.
  String? get domain => client.domainProven ? client.domain : null;

  static final _linkId = RegExp(r'^[A-Za-z0-9_-]{16,128}$');
  static final _nonce = RegExp(r'^[A-Za-z0-9_-]{16,128}$');
  static final _fingerprint = RegExp(r'^[0-9a-f]{16}$');
  static final _domain = RegExp(
      r'^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$');

  factory LinkRequest.fromJson(Map<String, Object?> json) {
    final id = json['link_id'];
    final nonce = json['nonce'];
    final expires = json['expires_at'];
    final client = json['client'];
    if (id is! String || !_linkId.hasMatch(id)) {
      throw const FormatException('link: link_id');
    }
    if (nonce is! String || !_nonce.hasMatch(nonce)) {
      throw const FormatException('link: nonce');
    }
    if (expires is! int) throw const FormatException('link: expires_at');
    if (client is! Map) throw const FormatException('link: client');
    final fingerprint = client['fingerprint'];
    final proven = client['domain_proven'];
    final domain = client['domain'];
    if (fingerprint is! String ||
        !_fingerprint.hasMatch(fingerprint) ||
        proven is! bool) {
      throw const FormatException('link: client');
    }
    if (proven) {
      // The host Biatec OIDC checked, in ASCII (A-label) form: what is drawn and what is signed are the same string.
      if (domain is! String ||
          domain.length > 253 ||
          !_domain.hasMatch(domain)) {
        throw const FormatException('link: client.domain');
      }
    } else if (domain != null) {
      throw const FormatException('link: client.domain');
    }
    final label = json['user_label'];
    if (label != null && !isRelyingPartyText(label, 40)) {
      throw const FormatException('link: user_label');
    }
    return LinkRequest(
      linkId: id,
      client: ConsentClient(
        domainProven: proven,
        domain: proven ? domain as String : null,
        fingerprint: fingerprint,
        linked: false,
      ),
      userLabel: label as String?,
      nonce: nonce,
      expiresAt: expires,
    );
  }
}
