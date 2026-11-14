/// EUR amounts are decimal STRINGS end to end (`"250"`, `"250.50"`): they are never parsed to a double, so no rounding can turn what the user typed into another amount. These helpers read
/// the digits as whole cents (a `BigInt`) only to compare two amounts for a sentence on the screen. They decide nothing: Biatec OIDC says whether a change loosens.
library;

/// The amount shape Biatec OIDC takes: `0` or no leading zero, at most 15 whole digits, at most two decimals.
final RegExp amountFormat = RegExp(r'^(0|[1-9][0-9]{0,14})(\.[0-9]{1,2})?$');

/// The whole cents of [amount], or null when it is not an amount.
BigInt? amountCents(String? amount) {
  if (amount == null) return null;
  final v = amount.trim();
  final m = RegExp(r'^([0-9]+)(?:\.([0-9]{1,2}))?$').firstMatch(v);
  if (m == null) return null;
  final cents = (m.group(2) ?? '').padRight(2, '0');
  return BigInt.parse(m.group(1)!) * BigInt.from(100) + BigInt.parse(cents);
}

/// Negative when [a] is less than [b], zero when equal, positive when more; null when either is not an amount.
int? compareAmounts(String? a, String? b) {
  final x = amountCents(a);
  final y = amountCents(b);
  return x == null || y == null ? null : x.compareTo(y);
}

/// `0`, `0.00`, `00`: asks for everything.
bool isZeroAmount(String? amount) => amountCents(amount) == BigInt.zero;
