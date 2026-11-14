/// The parts of a protocol 1.1 `biatec_transaction` detail: the outgoing
/// transfers, the applications and the EUR value. Parsed strictly: anything
/// the card could not show faithfully throws [FormatException].
library;

/// An amount in a currency, as the decimal string received.
class FiatValue {
  const FiatValue({required this.amount, required this.currency});

  final String amount;
  final String currency;

  @override
  String toString() => '$amount $currency';

  static FiatValue fromJson(Object? json, String where) {
    if (json is! Map) throw FormatException('detail: $where');
    final amount = json['amount'];
    final currency = json['currency'];
    if (amount is! String ||
        amount.isEmpty ||
        currency is! String ||
        currency.isEmpty) {
      throw FormatException('detail: $where');
    }
    return FiatValue(amount: amount, currency: currency);
  }
}

class AssetRef {
  const AssetRef(
      {required this.id, required this.symbol, required this.decimals});

  final String id;
  final String symbol;
  final int decimals;
}

/// One payment or asset transfer the sender sends out.
class TransferEntry {
  const TransferEntry({
    required this.receiver,
    required this.asset,
    required this.amount,
    this.fiat,
  });

  final String receiver;
  final AssetRef asset;

  /// Decimal string, shown verbatim.
  final String amount;

  /// Absent when the transfer could not be priced.
  final FiatValue? fiat;

  static TransferEntry fromJson(Object? json) {
    if (json is! Map) throw const FormatException('detail: transfer');
    final receiver = json['receiver'];
    final amount = json['amount'];
    final asset = json['asset'];
    if (receiver is! String ||
        receiver.isEmpty ||
        amount is! String ||
        amount.isEmpty ||
        asset is! Map) {
      throw const FormatException('detail: transfer');
    }
    final id = asset['id'];
    final symbol = asset['symbol'];
    final decimals = asset['decimals'];
    if (id is! String ||
        id.isEmpty ||
        symbol is! String ||
        symbol.isEmpty ||
        decimals is! int) {
      throw const FormatException('detail: transfer asset');
    }
    return TransferEntry(
      receiver: receiver,
      asset: AssetRef(id: id, symbol: symbol, decimals: decimals),
      amount: amount,
      fiat: json['fiat_value'] == null
          ? null
          : FiatValue.fromJson(json['fiat_value'], 'transfer fiat_value'),
    );
  }
}

/// One application the group calls. [trusted] is true only when the user
/// trusted it for this client on their own phone.
class ApplicationEntry {
  const ApplicationEntry({required this.id, required this.trusted});

  final String id;
  final bool trusted;

  static ApplicationEntry fromJson(Object? json) {
    if (json is! Map) throw const FormatException('detail: application');
    final id = json['id'];
    final trusted = json['trusted'];
    if ((id is! int && id is! String) || trusted is! bool) {
      throw const FormatException('detail: application');
    }
    return ApplicationEntry(id: '$id', trusted: trusted);
  }
}

/// The 1.1 members of a transaction detail.
class GroupFacts {
  const GroupFacts({
    required this.transfers,
    required this.applications,
    required this.valueKnown,
    required this.total,
    required this.origin,
  });

  final List<TransferEntry> transfers;
  final List<ApplicationEntry> applications;
  final bool valueKnown;

  /// Present exactly when [valueKnown].
  final FiatValue? total;
  final String origin;

  static GroupFacts fromJson(Map<String, Object?> json) {
    final transfers = json['transfers'];
    final applications = json['applications'];
    final known = json['value_known'];
    final origin = json['origin'];
    if (transfers is! List ||
        transfers.length > 16 ||
        applications is! List ||
        applications.length > 16 ||
        known is! bool ||
        origin is! String) {
      throw const FormatException('detail: transfers, applications or value');
    }
    // fiat_value is present exactly when value_known is.
    if ((json['fiat_value'] != null) != known) {
      throw const FormatException('detail: fiat_value and value_known differ');
    }
    return GroupFacts(
      transfers: [for (final t in transfers) TransferEntry.fromJson(t)],
      applications: [
        for (final a in applications) ApplicationEntry.fromJson(a)
      ],
      valueKnown: known,
      total:
          known ? FiatValue.fromJson(json['fiat_value'], 'fiat_value') : null,
      origin: origin,
    );
  }
}
