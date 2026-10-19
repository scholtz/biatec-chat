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
    final d = request.detail; // throws when the detail is malformed
    if (d.boundHash != request.boundHash) {
      throw const FormatException(
          'consent request: bound_hash differs from the detail hash');
    }
    return request;
  }
}

/// The requesting client. Display name and domain exist only for verified
/// clients (rule 3); an unverified one is shown by its fingerprint.
class ConsentClient {
  const ConsentClient({
    required this.entityId,
    required this.verified,
    this.displayName,
    this.domain,
    this.fingerprint,
  });

  final String entityId;
  final bool verified;
  final String? displayName;
  final String? domain;
  final String? fingerprint;

  factory ConsentClient.fromJson(Map<String, Object?> json) {
    final entityId = json['entity_id'];
    final verified = json['verified'];
    if (entityId is! String || verified is! bool) {
      throw const FormatException('consent request: client');
    }
    String? opt(String key) {
      final v = json[key];
      if (v != null && v is! String) {
        throw FormatException('consent request: client.$key');
      }
      return v as String?;
    }

    return ConsentClient(
      entityId: entityId,
      verified: verified,
      // Never take identity claims from an unverified client.
      displayName: verified ? opt('display_name') : null,
      domain: verified ? opt('domain') : null,
      fingerprint: opt('fingerprint'),
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
    this.receiver,
    this.applicationId,
    required this.assetId,
    required this.assetSymbol,
    required this.assetDecimals,
    required this.amount,
    required this.fiatAmount,
    required this.fiatCurrency,
  });

  final String network;
  final String groupHash;
  final String sender;
  final String? receiver;
  final int? applicationId;
  final String assetId;
  final String assetSymbol;
  final int assetDecimals;

  /// Decimal string, shown verbatim.
  final String amount;
  final String fiatAmount;
  final String fiatCurrency;

  @override
  String get boundHash => groupHash;

  factory TransactionDetail.fromJson(Map<String, Object?> json) {
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
