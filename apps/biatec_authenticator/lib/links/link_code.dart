/// The link code a relying party shows as a QR code (specification 14.3 step 2): `biatec-authenticator://link?iss=<issuer>&id=<link_id>&s=<link_secret>`.
///
/// It is an input from outside the app, scanned or opened from another app, so it is parsed strictly and never acted on without the link screen: the scheme, the host `link`, exactly the three
/// parameters `iss`, `id` and `s` (each once, none empty, nothing else, no path, port, user name or fragment) and the shape of each value. `iss` must be the origin of the Biatec OIDC this
/// app is enrolled with, exactly; the check is made before the link secret `s` goes anywhere.
library;

/// Why a code is refused.
enum LinkCodeProblem {
  /// Not a link code of this app: another scheme or host, a missing, repeated, unknown or empty parameter, a value of the wrong shape.
  notALinkCode,

  /// A link code, but for another authenticator service than the one this app is enrolled with.
  differentService,
}

class LinkCodeException implements Exception {
  const LinkCodeException(this.problem);
  final LinkCodeProblem problem;

  @override
  String toString() => 'LinkCodeException: ${problem.name}';
}

/// A parsed, checked link code.
class LinkCode {
  const LinkCode(
      {required this.issuer, required this.linkId, required this.secret});

  /// The issuer in the code, which equals the enrolled server's origin.
  final String issuer;
  final String linkId;

  /// The 32 random bytes of the link secret as base64url (43 characters). It is a bearer secret for five minutes: it is sent to the enrolled server only, in a header, and never shown, logged or stored.
  final String secret;

  static const scheme = 'biatec-authenticator';
  static const host = 'link';

  static final _id = RegExp(r'^[A-Za-z0-9_-]{16,128}$');
  static final _secret = RegExp(r'^[A-Za-z0-9_-]{43}$');

  /// Parses [raw]. [enrolledOrigin] is the origin of the Biatec OIDC this app is enrolled with ([originOf] of its base URL); [extraIssuers] are exact issuer strings that are accepted as well, which only
  /// the development mock needs (its issuer is a fixed name, not the URL it is served from). Throws [LinkCodeException].
  static LinkCode parse(String raw,
      {required String enrolledOrigin, Set<String> extraIssuers = const {}}) {
    const notACode = LinkCodeException(LinkCodeProblem.notALinkCode);
    // No white space or control character anywhere, and no fragment: what was scanned is what is read.
    if (raw.isEmpty ||
        raw.length > 1024 ||
        raw.runes.any((r) => r <= 0x20 || r == 0x7F) ||
        raw.contains('#')) {
      throw notACode;
    }
    // A percent sign that does not start an escape is not decoded leniently: such a code is not one.
    if (RegExp(r'%(?![0-9A-Fa-f]{2})').hasMatch(raw)) throw notACode;
    final uri = Uri.tryParse(raw);
    if (uri == null ||
        uri.scheme != scheme ||
        uri.host != host ||
        uri.hasPort ||
        uri.userInfo.isNotEmpty ||
        uri.path.isNotEmpty ||
        !uri.hasQuery ||
        uri.hasFragment) {
      throw notACode;
    }
    // The raw query, member by member: the first reading of a repeated name must not be able to hide another.
    final values = <String, String>{};
    for (final member in uri.query.split('&')) {
      final eq = member.indexOf('=');
      if (eq <= 0 || eq == member.length - 1) throw notACode;
      final name = member.substring(0, eq);
      if (name != 'iss' && name != 'id' && name != 's') throw notACode;
      if (values.containsKey(name)) throw notACode;
      try {
        values[name] = Uri.decodeQueryComponent(member.substring(eq + 1));
      } on Object {
        throw notACode;
      }
    }
    if (values.length != 3) throw notACode;
    final id = values['id']!;
    final secret = values['s']!;
    final iss = values['iss']!;
    if (!_id.hasMatch(id) || !_secret.hasMatch(secret)) throw notACode;
    if (iss != enrolledOrigin && !extraIssuers.contains(iss)) {
      throw const LinkCodeException(LinkCodeProblem.differentService);
    }
    return LinkCode(issuer: iss, linkId: id, secret: secret);
  }

  /// A deep link or a pasted text that is not (yet) a code: used by the entry points to tell a link code from anything else.
  static bool looksLikeLinkCode(String raw) =>
      raw.trim().toLowerCase().startsWith('$scheme:');
}

/// The origin of [url] as the issuer of a Biatec OIDC is written: `scheme://host` with the port only when it is not the default of the scheme, scheme and host in lower case. Anything that is not an
/// absolute http or https URL with a host gives the empty string, which equals no issuer.
String originOf(String url) {
  final uri = Uri.tryParse(url);
  if (uri == null ||
      !uri.hasAuthority ||
      uri.host.isEmpty ||
      (uri.scheme != 'https' && uri.scheme != 'http')) {
    return '';
  }
  final defaultPort = uri.scheme == 'https' ? 443 : 80;
  final port = uri.hasPort && uri.port != defaultPort ? ':${uri.port}' : '';
  final host = uri.host.contains(':') ? '[${uri.host}]' : uri.host;
  return '${uri.scheme}://$host$port';
}
