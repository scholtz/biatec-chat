part of '../consent_request.dart';

/// `details_hash` (specification 3.6): the base64url encoding without padding of the SHA-256 of the RFC 8785 canonical form of the `authorization_details` OBJECT (one object, not the array)
/// as it is held, including the `origin` Biatec OIDC added. The device computes it itself from the object it renders and signs, and never copies it from the request.
String computeDetailsHash(Map<String, Object?> details) =>
    base64Url.encode(sha256.convert(canonicalBytes(details)).bytes).replaceAll('=', '');

/// An IBAN in electronic form (upper case, no spaces, 15 to 34 characters) whose ISO 7064 mod-97-10 check digits are right. Country lengths are not checked.
bool isValidIban(Object? iban) {
  if (iban is! String || !RegExp(r'^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$').hasMatch(iban)) {
    return false;
  }
  var rem = 0;
  for (final unit in (iban.substring(4) + iban.substring(0, 4)).codeUnits) {
    // Letters are two digits, A = 10 ... Z = 35.
    rem = unit >= 0x41 ? (rem * 100 + unit - 55) % 97 : (rem * 10 + unit - 0x30) % 97;
  }
  return rem == 1;
}

/// A decimal string of at most two decimals, greater than zero. Never a number.
bool isValidPaymentAmount(Object? amount) =>
    amount is String &&
    RegExp(r'^(0|[1-9][0-9]{0,14})(\.[0-9]{1,2})?$').hasMatch(amount) &&
    RegExp(r'[1-9]').hasMatch(amount);

/// A calendar date `YYYY-MM-DD` that exists.
bool isValidDate(Object? text) {
  final m = text is String ? RegExp(r'^([0-9]{4})-([0-9]{2})-([0-9]{2})$').firstMatch(text) : null;
  if (m == null) return false;
  final y = int.parse(m[1]!), mo = int.parse(m[2]!), d = int.parse(m[3]!);
  if (y < 1 || mo < 1 || mo > 12 || d < 1) return false;
  final t = DateTime.utc(y, mo, d);
  return t.year == y && t.month == mo && t.day == d;
}

/// A canonical origin: scheme, lower-case host of well-formed labels, optional port 1-65535 that is not the scheme's default.
final RegExp _originShape = RegExp(
    r'^(?!https://[^/]*:443$)(?!http://[^/]*:80$)https?://[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*(:([1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?$');

bool _onlyMembers(Object? o, List<String> required, List<String> optional) =>
    o is Map &&
    required.every(o.containsKey) &&
    o.keys.every((k) => required.contains(k) || optional.contains(k));

bool _sameMembers(Object? o, List<String> keys) =>
    o is Map && o.length == keys.length && keys.every(o.containsKey);

/// A payment, login or action of a relying party (protocol 1.2, specification 3.3 to 3.5). [object] is the `authorization_details` object exactly as received (with the `origin`
/// Biatec OIDC added, when it had one): it is what is drawn, hashed and signed. Parsed strictly, the way `isValidRelyingPartyDetails` of the protocol kit does: the members of the schema
/// and nothing else, the text rules of chapter 15 for every string the service wrote, the IBAN check digits, the amount and the date.
sealed class RelyingPartyDetail extends AuthorizationDetail {
  RelyingPartyDetail(this.object);

  /// The object as received.
  final Map<String, Object?> object;

  /// The origin Biatec OIDC recorded inside the details, or null.
  String? get origin => object['origin'] as String?;

  /// The hash the device computed from [object] itself.
  late final String detailsHash = computeDetailsHash(object);

  @override
  String get boundHash => detailsHash;

  /// The detail, or null when it is not exactly one of the three types as the schemas describe it.
  static RelyingPartyDetail? tryParse(Map<String, Object?> json) {
    try {
      if (json.containsKey('origin')) {
        final o = json['origin'];
        if (o is! String || o.length > 253 || !_originShape.hasMatch(o)) return null;
      }
      switch (json['type']) {
        case 'biatec_payment':
          return PaymentDetail._parse(json);
        case 'biatec_login':
          return LoginDetail._parse(json);
        case 'biatec_action':
          return ActionDetail._parse(json);
      }
    } on Object {
      // Anything that does not have the shape throws somewhere above: it is not a detail this app can show.
    }
    return null;
  }
}

/// `biatec_payment`: the relying party (a bank) executes it after it has the user's signed approval; Biatec does not.
class PaymentDetail extends RelyingPartyDetail {
  PaymentDetail._(
    super.object, {
    required this.amount,
    required this.currency,
    required this.creditorName,
    this.iban,
    this.identification,
    this.scheme,
    this.remittance,
    this.executionDate,
  });

  /// A decimal string of at most two decimals, shown verbatim.
  final String amount;

  /// Three upper-case letters (ISO 4217).
  final String currency;
  final String creditorName;

  /// Either [iban], or [identification] with its [scheme].
  final String? iban;
  final String? identification;
  final String? scheme;
  final String? remittance;

  /// `YYYY-MM-DD`.
  final String? executionDate;

  /// The IBAN in groups of four, the way it is read out and compared.
  String? get ibanGrouped => iban == null
      ? null
      : [for (var i = 0; i < iban!.length; i += 4) iban!.substring(i, i + 4 > iban!.length ? iban!.length : i + 4)].join(' ');

  static PaymentDetail? _parse(Map<String, Object?> j) {
    if (!_onlyMembers(j, const ['type', 'instructed_amount', 'creditor_name', 'creditor_account'],
        const ['remittance', 'execution_date', 'origin'])) {
      return null;
    }
    final a = j['instructed_amount'];
    if (!_sameMembers(a, const ['amount', 'currency'])) return null;
    final amount = (a as Map)['amount'];
    final currency = a['currency'];
    if (!isValidPaymentAmount(amount) || currency is! String || !RegExp(r'^[A-Z]{3}$').hasMatch(currency)) {
      return null;
    }
    final name = j['creditor_name'];
    if (!isRelyingPartyText(name, 70)) return null;
    final c = j['creditor_account'];
    String? iban, identification, scheme;
    if (_sameMembers(c, const ['iban'])) {
      iban = (c as Map)['iban'] as String?;
      if (!isValidIban(iban)) return null;
    } else if (_sameMembers(c, const ['identification', 'scheme'])) {
      identification = (c as Map)['identification'] as String?;
      scheme = c['scheme'] as String?;
      if (!isRelyingPartyText(identification, 34) || !isRelyingPartyText(scheme, 20)) return null;
    } else {
      return null;
    }
    final remittance = j['remittance'];
    if (j.containsKey('remittance') && !isRelyingPartyText(remittance, 140)) return null;
    final date = j['execution_date'];
    if (j.containsKey('execution_date') && !isValidDate(date)) return null;
    return PaymentDetail._(j,
        amount: amount as String,
        currency: currency,
        creditorName: name as String,
        iban: iban,
        identification: identification,
        scheme: scheme,
        remittance: remittance as String?,
        executionDate: date as String?);
  }
}

/// `biatec_login`: access to the relying party's own service. The context is what the service says about the session that asks.
class LoginDetail extends RelyingPartyDetail {
  LoginDetail._(super.object, {this.ip, this.location, this.userAgent});

  final String? ip;
  final String? location;
  final String? userAgent;

  /// The reported members that are present, as (label key, value), in the order of the specification.
  List<(String, String)> get context => [
        if (ip != null) ('ip', ip!),
        if (location != null) ('location', location!),
        if (userAgent != null) ('userAgent', userAgent!),
      ];

  static LoginDetail? _parse(Map<String, Object?> j) {
    if (!_onlyMembers(j, const ['type'], const ['context', 'origin'])) return null;
    if (!j.containsKey('context')) return LoginDetail._(j);
    final c = j['context'];
    if (c is! Map || c.isEmpty || !_onlyMembers(c, const [], const ['ip', 'location', 'user_agent'])) {
      return null;
    }
    if (!c.values.every((v) => isRelyingPartyText(v, 80))) return null;
    return LoginDetail._(j,
        ip: c['ip'] as String?,
        location: c['location'] as String?,
        userAgent: c['user_agent'] as String?);
  }
}

/// `biatec_action`: any other action, as a title and labelled lines the service wrote; nothing else is shown.
class ActionDetail extends RelyingPartyDetail {
  ActionDetail._(super.object, {required this.title, required this.lines});

  final String title;

  /// 1 to 8 lines of (label, value).
  final List<(String, String)> lines;

  static ActionDetail? _parse(Map<String, Object?> j) {
    if (!_onlyMembers(j, const ['type', 'title', 'lines'], const ['origin'])) return null;
    final title = j['title'];
    if (!isRelyingPartyText(title, 60)) return null;
    final l = j['lines'];
    if (l is! List || l.isEmpty || l.length > 8) return null;
    final lines = <(String, String)>[];
    for (final x in l) {
      if (!_sameMembers(x, const ['label', 'value'])) return null;
      final label = (x as Map)['label'];
      final value = x['value'];
      if (!isRelyingPartyText(label, 30) || !isRelyingPartyText(value, 80)) return null;
      lines.add((label as String, value as String));
    }
    return ActionDetail._(j, title: title as String, lines: lines);
  }
}

/// A detail this app cannot show: an unknown type, a relying-party type of a build without them, a schema failure or a text-rule violation. Nothing of it is drawn and it cannot be approved or
/// denied here (a receipt over it could not be a valid one).
class UnsupportedDetail extends AuthorizationDetail {
  const UnsupportedDetail(this.reason);

  /// For tests and diagnostics only; never shown.
  final String reason;

  @override
  String get boundHash => '';
}
