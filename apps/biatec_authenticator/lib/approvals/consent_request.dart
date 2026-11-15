import 'dart:convert';

import 'package:crypto/crypto.dart';

import '../links/text_rules.dart';
import 'jcs.dart';
import 'models/transfer_models.dart';

export 'models/transfer_models.dart';

part 'models/relying_party_models.dart';

/// The consent request object fetched from Biatec OIDC
/// (`schemas/consent-request.schema.json`). It never carries the binding code.
///
/// The card is rendered from this object only, and the receipt signs
/// [authorizationDetails] exactly as fetched (rule 1: what you see is what you
/// sign), so the raw JSON is kept alongside the parsed view.
class ConsentRequest {
  ConsentRequest._({
    required this.json,
    required this.authReqId,
    required this.entry,
    required this.authorizationDetails,
    required this.client,
    required this.bindingMessage,
    required this.expiry,
    required this.boundHash,
    required this.origin,
    required this.sub,
    required this.relyingParties,
  });

  /// The object as received.
  final Map<String, Object?> json;

  final String authReqId;

  /// `step-up` or `direct`.
  final String entry;

  /// Exactly one authorization detail, as received.
  final List<Object?> authorizationDetails;

  final ConsentClient client;

  /// Client text to render, or the empty string. Biatec OIDC sends the empty
  /// string for unverified clients; the app also never renders it for them.
  final String bindingMessage;

  /// Unix seconds.
  final int expiry;
  final String boundHash;

  /// The request's `origin`. Empty when the server had none, which only a relying-party request (protocol 1.2) can have.
  final String origin;
  final String sub;

  /// `AppConfig.relyingPartiesEnabled`: the payment, login and action types are shown. Off, a request of one of them is an [UnsupportedDetail]: it cannot be shown or approved.
  final bool relyingParties;

  DateTime get expiresAt =>
      DateTime.fromMillisecondsSinceEpoch(expiry * 1000, isUtc: true);

  /// Whether the card can show this request in full: a protocol 1.1 request
  /// (transfers, applications, value) or a relying-party request of protocol 1.2 whose
  /// text passed the text rules. A 1.0 request, or any request whose
  /// client or detail is of the 1.0 shape, an unknown type or a request that fails its schema is refused and never approvable.
  bool get isShowable {
    if (client.legacy) return false;
    final d = detail;
    return d is LimitsChangeDetail ||
        d is RelyingPartyDetail ||
        (d is TransactionDetail && !d.isLegacy);
  }

  /// A payment, login or action request of a relying party, shown in full.
  bool get isRelyingParty => !client.legacy && detail is RelyingPartyDetail;

  bool isExpired(DateTime now) => !expiresAt.isAfter(now);

  /// A receipt's `remember` holds at most this many application ids (the schema's `maxItems`).
  static const maxRemembered = 16;

  /// Every application of this request that could be offered, before the cap: untrusted, id 1 or more (0 is an application creation, never trusted), and listed once (a verifier refuses an id
  /// that is ambiguous). A 1.0 request offers none.
  late final List<ApplicationEntry> _offerable = () {
    final d = detail;
    if (!isShowable || d is! TransactionDetail) return const <ApplicationEntry>[];
    final apps = d.group!.applications;
    return [
      for (final a in apps)
        if (!a.trusted &&
            (int.tryParse(a.id) ?? 0) >= 1 &&
            apps.where((o) => o.id == a.id).length == 1)
          a
    ];
  }();

  /// What the card offers to trust ("don't ask again"): at most [maxRemembered] applications. Computed once.
  late final List<ApplicationEntry> rememberable =
      _offerable.length <= maxRemembered
          ? _offerable
          : _offerable.sublist(0, maxRemembered);

  /// More applications could be trusted than the card offers (the cap); the card says so.
  bool get rememberableTruncated => _offerable.length > maxRemembered;

  /// Whether [a] is one the card offers to trust.
  bool canRemember(ApplicationEntry a) => rememberable.any((o) => o.id == a.id);

  /// The single detail, parsed for display. A relying-party request is also checked as a whole (spec 6, 8.4 step 5): a direct request, no binding message, a linked client and an
  /// `origin` equal to the one inside the details; one that is not is an [UnsupportedDetail].
  late final AuthorizationDetail detail = _detailOf();

  AuthorizationDetail _detailOf() {
    final d = AuthorizationDetail.parse(
        (authorizationDetails.single as Map).cast<String, Object?>(),
        relyingParties: relyingParties);
    if (d is! RelyingPartyDetail) return d;
    final inside = d.origin;
    final consistent = entry == 'direct' &&
        bindingMessage.isEmpty &&
        !client.legacy &&
        client.linked &&
        (inside ?? '') == origin;
    return consistent ? d : const UnsupportedDetail('inconsistent request');
  }

  /// Parses and checks [json]. Throws [FormatException] on anything the card
  /// could not show faithfully, so a malformed request is never approvable.
  ///
  /// [relyingParties] says whether the three relying-party types are shown (`AppConfig.relyingPartiesEnabled`); off, they are not approvable.
  factory ConsentRequest.fromJson(Map<String, Object?> json,
      {bool relyingParties = false}) {
    String str(String key) {
      final v = json[key];
      if (v is! String) throw FormatException('consent request: $key');
      return v;
    }

    final details = json['authorization_details'];
    if (details is! List || details.length != 1 || details.single is! Map) {
      throw const FormatException(
          'consent request: exactly one authorization detail is required');
    }
    final expiry = json['expiry'];
    if (expiry is! int) throw const FormatException('consent request: expiry');
    final client = json['client'];
    if (client is! Map) throw const FormatException('consent request: client');
    final request = ConsentRequest._(
      json: json,
      authReqId: str('auth_req_id'),
      entry: str('entry'),
      authorizationDetails: details,
      client: ConsentClient.fromJson(client.cast<String, Object?>()),
      bindingMessage: str('binding_message'),
      expiry: expiry,
      boundHash: str('bound_hash'),
      origin: json['origin'] == null ? '' : str('origin'),
      sub: str('sub'),
      relyingParties: relyingParties,
    );
    try {
      // A string the receipt could not sign must not reach the card.
      canonicalize(json);
    } on ArgumentError catch (e) {
      throw FormatException('consent request: ${e.message}');
    }
    final d = request.detail; // throws when the detail is malformed
    // A wallet request always names its origin; only a relying party's request may have none.
    if (json['origin'] == null && d is! UnsupportedDetail && d is! RelyingPartyDetail) {
      throw const FormatException('consent request: origin');
    }
    // For a relying-party type this is the hash the app computed itself from the object it renders (spec 3.6), never one copied from the request: a request that states another is refused.
    if (d is! UnsupportedDetail && d.boundHash != request.boundHash) {
      throw const FormatException(
          'consent request: bound_hash differs from the detail hash');
    }
    return request;
  }
}

/// The requesting client. Protocol 1.1: `{ fingerprint, domain_proven,
/// domain?, linked }`; only a proven domain says who it is, and nothing here
/// is a name (rule 3). The 1.0 shape (`entity_id`, `verified`, `display_name`)
/// still parses so an old request can be recognised and refused; of it only the
/// `entity_id` is kept, because a deny receipt of a 1.0 request names the client by it, and none of it is ever shown.
class ConsentClient {
  const ConsentClient({
    this.entityId,
    required this.domainProven,
    this.domain,
    this.fingerprint,
    this.linked = false,
    this.legacy = false,
    this.legacyVerified = false,
  });

  /// 1.0 only, signed into a 1.0 receipt and never rendered.
  final String? entityId;

  /// The client proved control of [domain]. A 1.0 client is never proven.
  final bool domainProven;

  /// The proven ASCII host, only when [domainProven].
  final String? domain;
  final String? fingerprint;

  /// This user already linked the client on their phone.
  final bool linked;

  /// A 1.0 client object.
  final bool legacy;

  /// 1.0 only: the `verified` flag, kept because a 1.0 receipt signs the binding message only for such a client. Never rendered.
  final bool legacyVerified;

  /// `xxxx-xxxx-xxxx-xxxx` for the 16 hex characters, otherwise as received.
  String? get fingerprintShown {
    final f = fingerprint;
    if (f == null || f.length != 16) return f;
    return [for (var i = 0; i < 16; i += 4) f.substring(i, i + 4)].join('-');
  }

  factory ConsentClient.fromJson(Map<String, Object?> json) {
    String? opt(String key) {
      final v = json[key];
      if (v != null && v is! String) {
        throw FormatException('consent request: client.$key');
      }
      return v as String?;
    }

    if (json.containsKey('domain_proven')) {
      final proven = json['domain_proven'];
      final linked = json['linked'];
      final fingerprint = json['fingerprint'];
      if (proven is! bool ||
          linked is! bool ||
          fingerprint is! String ||
          fingerprint.isEmpty) {
        throw const FormatException('consent request: client');
      }
      final domain = opt('domain');
      if (proven && (domain == null || domain.isEmpty)) {
        throw const FormatException('consent request: client.domain');
      }
      return ConsentClient(
        domainProven: proven,
        // No domain claim is taken from a client that did not prove it.
        domain: proven ? domain : null,
        fingerprint: fingerprint,
        linked: linked,
      );
    }
    final entityId = json['entity_id'];
    final verified = json['verified'];
    if (entityId is! String || verified is! bool) {
      throw const FormatException('consent request: client');
    }
    return ConsentClient(
      entityId: entityId,
      domainProven: false,
      fingerprint: opt('fingerprint'),
      legacy: true,
      legacyVerified: verified,
    );
  }
}

/// One parsed authorization detail.
sealed class AuthorizationDetail {
  const AuthorizationDetail();

  /// The hash the consent is bound to (`group_hash` or `request_hash`).
  String get boundHash;

  /// An unknown type, a relying-party type of a build that does not have them, or a relying-party detail that fails its schema or the text rules is an [UnsupportedDetail]: nothing of it is
  /// drawn and it cannot be approved. A wallet type that is malformed throws, as it always did.
  static AuthorizationDetail parse(Map<String, Object?> json,
      {bool relyingParties = false}) {
    switch (json['type']) {
      case 'biatec_transaction':
        return TransactionDetail.fromJson(json);
      case 'biatec_limits_change':
        return LimitsChangeDetail.fromJson(json);
      case 'biatec_payment' || 'biatec_login' || 'biatec_action':
        if (!relyingParties) return const UnsupportedDetail('not enabled');
        return RelyingPartyDetail.tryParse(json) ??
            const UnsupportedDetail('schema or text rules');
      default:
        return const UnsupportedDetail('unknown type');
    }
  }
}

String _req(Map<String, Object?> json, String key) {
  final v = json[key];
  if (v is! String || v.isEmpty) throw FormatException('detail: $key');
  return v;
}

Map<String, Object?> _obj(Map<String, Object?> json, String key) {
  final v = json[key];
  if (v is! Map) throw FormatException('detail: $key');
  return v.cast<String, Object?>();
}

class TransactionDetail extends AuthorizationDetail {
  const TransactionDetail({
    required this.network,
    required this.groupHash,
    required this.sender,
    this.group,
    this.receiver,
    this.applicationId,
    this.assetId,
    this.assetSymbol,
    this.assetDecimals,
    this.amount,
    this.fiatAmount,
    this.fiatCurrency,
  });

  final String network;
  final String groupHash;
  final String sender;

  /// Protocol 1.1: every transfer and application. Null for a 1.0 detail,
  /// which this app cannot show and therefore refuses.
  final GroupFacts? group;

  // The 1.0 single-transfer members.
  final String? receiver;
  final int? applicationId;
  final String? assetId;
  final String? assetSymbol;
  final int? assetDecimals;
  final String? amount;
  final String? fiatAmount;
  final String? fiatCurrency;

  /// A 1.0 detail: no `transfers`.
  bool get isLegacy => group == null;

  @override
  String get boundHash => groupHash;

  /// The amount line for lists and history.
  String get headline {
    final g = group;
    if (g == null) return '$amount $assetSymbol';
    if (g.transfers.length == 1) {
      final t = g.transfers.single;
      return '${t.amount} ${t.asset.symbol}';
    }
    if (g.transfers.isEmpty) return 'No transfers';
    return '${g.transfers.length} transfers';
  }

  /// The value line: the group total, or "Value unknown".
  String get valueText {
    final g = group;
    if (g == null) return '$fiatAmount $fiatCurrency';
    return g.valueKnown ? '${g.total}' : 'Value unknown';
  }

  factory TransactionDetail.fromJson(Map<String, Object?> json) {
    if (json.containsKey('transfers')) {
      return TransactionDetail(
        network: _req(json, 'network'),
        groupHash: _req(json, 'group_hash'),
        sender: _req(json, 'sender'),
        group: GroupFacts.fromJson(json),
      );
    }
    final asset = _obj(json, 'asset');
    final fiat = _obj(json, 'fiat_value');
    final receiver = json['receiver'];
    final app = json['application_id'];
    final decimals = asset['decimals'];
    if ((receiver != null && (receiver is! String || receiver.isEmpty)) ||
        (app != null && app is! int) ||
        decimals is! int) {
      throw const FormatException('detail: receiver, application or asset');
    }
    return TransactionDetail(
      network: _req(json, 'network'),
      groupHash: _req(json, 'group_hash'),
      sender: _req(json, 'sender'),
      receiver: receiver as String?,
      applicationId: app as int?,
      assetId: _req(asset, 'id'),
      assetSymbol: _req(asset, 'symbol'),
      assetDecimals: decimals,
      amount: _req(json, 'amount'),
      fiatAmount: _req(fiat, 'amount'),
      fiatCurrency: _req(fiat, 'currency'),
    );
  }
}

class Limits {
  const Limits({required this.perTransactionEur, required this.perDayEur});
  final String perTransactionEur;
  final String perDayEur;

  factory Limits.fromJson(Map<String, Object?> json) => Limits(
        perTransactionEur: _req(json, 'per_transaction_eur'),
        perDayEur: _req(json, 'per_day_eur'),
      );
}

class LimitsChangeDetail extends AuthorizationDetail {
  const LimitsChangeDetail({
    required this.current,
    required this.requested,
    required this.requestHash,
  });

  final Limits current;
  final Limits requested;
  final String requestHash;

  @override
  String get boundHash => requestHash;

  factory LimitsChangeDetail.fromJson(Map<String, Object?> json) =>
      LimitsChangeDetail(
        current: Limits.fromJson(_obj(json, 'current_limits')),
        requested: Limits.fromJson(_obj(json, 'requested_limits')),
        requestHash: _req(json, 'request_hash'),
      );
}
