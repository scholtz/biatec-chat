import 'jcs.dart';
import 'models/transfer_models.dart';

export 'models/transfer_models.dart';

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
  final String origin;
  final String sub;

  DateTime get expiresAt =>
      DateTime.fromMillisecondsSinceEpoch(expiry * 1000, isUtc: true);

  /// Whether the card can show this request in full: a protocol 1.1 request
  /// (transfers, applications, value). A 1.0 request, or any request whose
  /// client or detail is of the 1.0 shape, is refused and never approvable.
  bool get isShowable {
    if (client.legacy) return false;
    final d = detail;
    return d is LimitsChangeDetail || (d is TransactionDetail && !d.isLegacy);
  }

  bool isExpired(DateTime now) => !expiresAt.isAfter(now);

  /// The single detail, parsed for display.
  late final AuthorizationDetail detail = AuthorizationDetail.parse(
      (authorizationDetails.single as Map).cast<String, Object?>());

  /// Parses and checks [json]. Throws [FormatException] on anything the card
  /// could not show faithfully, so a malformed request is never approvable.
  factory ConsentRequest.fromJson(Map<String, Object?> json) {
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
      origin: str('origin'),
      sub: str('sub'),
    );
    try {
      // A string the receipt could not sign must not reach the card.
      canonicalize(json);
    } on ArgumentError catch (e) {
      throw FormatException('consent request: ${e.message}');
    }
    final d = request.detail; // throws when the detail is malformed
    if (d.boundHash != request.boundHash) {
      throw const FormatException(
          'consent request: bound_hash differs from the detail hash');
    }
    return request;
  }
}

/// The requesting client. Protocol 1.1: `{ fingerprint, domain_proven,
/// domain?, linked }`; only a proven domain says who it is, and nothing here
/// is a name (rule 3). The 1.0 shape (`entity_id`, `verified`, `display_name`)
/// still parses so an old request can be recognised and refused.
class ConsentClient {
  const ConsentClient({
    this.entityId,
    required this.verified,
    this.displayName,
    this.domain,
    this.fingerprint,
    this.linked = false,
    this.legacy = false,
  });

  /// 1.0 only.
  final String? entityId;

  /// 1.1: the domain is proven. 1.0: the operator vouched for the client.
  final bool verified;

  /// 1.0 only; never shown.
  final String? displayName;

  /// The proven ASCII host, only when [verified] in 1.1.
  final String? domain;
  final String? fingerprint;
  final bool linked;

  /// A 1.0 client object.
  final bool legacy;

  bool get domainProven => !legacy && verified;

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
        verified: proven,
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
      verified: verified,
      displayName: verified ? opt('display_name') : null,
      domain: verified ? opt('domain') : null,
      fingerprint: opt('fingerprint'),
      legacy: true,
    );
  }
}

/// One parsed authorization detail.
sealed class AuthorizationDetail {
  const AuthorizationDetail();

  /// The hash the consent is bound to (`group_hash` or `request_hash`).
  String get boundHash;

  static AuthorizationDetail parse(Map<String, Object?> json) {
    switch (json['type']) {
      case 'biatec_transaction':
        return TransactionDetail.fromJson(json);
      case 'biatec_limits_change':
        return LimitsChangeDetail.fromJson(json);
      default:
        throw FormatException(
            'unsupported authorization detail type ${json['type']}');
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
