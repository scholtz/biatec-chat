import 'dart:typed_data';

import 'package:crypto/crypto.dart';

import 'otp_account.dart';

/// Makes one-time codes: HOTP (RFC 4226) and TOTP (RFC 6238).
abstract final class OtpGenerator {
  static Hash _hash(OtpAlgorithm a) => switch (a) {
        OtpAlgorithm.sha1 => sha1,
        OtpAlgorithm.sha256 => sha256,
        OtpAlgorithm.sha512 => sha512,
      };

  /// HOTP: the code for [counter] (RFC 4226 section 5.3), as text with leading zeros.
  static String hotp(
    Uint8List secret,
    int counter, {
    OtpAlgorithm algorithm = OtpAlgorithm.sha1,
    int digits = 6,
  }) {
    // The account's own check, so no layer makes codes from values an account refuses (a guessable key, say).
    OtpLimits.checkCodeInputs(secret.length, digits, counter);
    // The counter is 8 bytes, big endian, written as two 32-bit halves (setUint64 does not exist when compiled to JavaScript).
    final message = Uint8List(8);
    ByteData.view(message.buffer)
      ..setUint32(0, counter ~/ 0x100000000)
      ..setUint32(4, counter % 0x100000000);
    // package:crypto's Hmac keeps its own padded copy of the key until it is collected; it cannot be wiped from here, and the rules forbid our own HMAC. The account's copy is wiped on lock.
    final mac = Hmac(_hash(algorithm), secret).convert(message).bytes;
    final offset = mac[mac.length - 1] & 0x0F;
    final binary = ((mac[offset] & 0x7F) << 24) |
        (mac[offset + 1] << 16) |
        (mac[offset + 2] << 8) |
        mac[offset + 3];
    return (binary % _modulus[digits - OtpLimits.minDigits])
        .toString()
        .padLeft(digits, '0');
  }

  /// 10 to the power of 6, 7 and 8 digits.
  static const _modulus = [1000000, 10000000, 100000000];

  /// The earliest time a working clock can show: before it, the clock was reset (a flat battery, say) and every time-based code is wrong. For the code list (T-224) to check, so it tells the user instead of showing codes.
  static final earliestPlausibleTime = DateTime.utc(2026);

  /// How far before [notBefore] the clock may still be, so a time saved while the clock ran a little fast does not mark the corrected clock as reset.
  static const clockSlack = Duration(days: 2);

  /// False when [now] is before [notBefore] less [clockSlack] (or before [earliestPlausibleTime]): the codes would be wrong. The caller passes the latest time it has seen (the vault's last save, say), so a clock reset to a date after the default floor is still caught. A clock running ahead cannot be told from here without a time source, so it is not judged.
  static bool clockIsPlausible(DateTime now, {DateTime? notBefore}) {
    final seen = notBefore?.subtract(clockSlack);
    final floor = seen != null && seen.isAfter(earliestPlausibleTime)
        ? seen
        : earliestPlausibleTime;
    return !now.isBefore(floor);
  }

  static void _timeBased(OtpAccount account) {
    if (account.type != OtpType.totp) {
      throw StateError(
          'A counter-based account has no time step, countdown or next code.');
    }
  }

  /// Milliseconds since the epoch, never below 0 (a reset clock), so the code, the seconds left and the ring agree. The value does not depend on the time zone.
  static int _millis(DateTime now) {
    final ms = now.millisecondsSinceEpoch;
    return ms < 0 ? 0 : ms;
  }

  static int _seconds(DateTime now) => _millis(now) ~/ 1000;

  /// The step a time-based account is in at [now]: seconds since the epoch divided by the period.
  static int step(OtpAccount account, DateTime now) {
    _timeBased(account);
    // A clock before 1970 (a reset real-time clock) counts as 1970 instead of failing in the middle of drawing the list; clockIsPlausible is for the list to check first.
    return _seconds(now) ~/ account.period;
  }

  /// The code of [account] at [now]. For a counter-based account that is the code for its current counter, whatever the time.
  static String code(OtpAccount account, DateTime now) => _codeFor(account,
      account.type == OtpType.hotp ? account.counter : step(account, now));

  /// The code of the following period (a time-based account), to show in the last seconds of this one. A counter-based account has no next code to show: its next code is only for after the current one was used.
  static String nextCode(OtpAccount account, DateTime now) =>
      // step refuses a counter-based account.
      _codeFor(account, step(account, now) + 1);

  static String _codeFor(OtpAccount account, int value) {
    // The secret getter refuses a wiped account.
    return hotp(account.secret, value,
        algorithm: account.algorithm, digits: account.digits);
  }

  /// Whole seconds left until the code changes, 1 to [OtpAccount.period] (a time-based account).
  static int secondsLeft(OtpAccount account, DateTime now) {
    _timeBased(account);
    return account.period - (_seconds(now) % account.period);
  }

  /// A fraction from 1 (just started) to 0 (about to change), for a countdown ring.
  static double fractionLeft(OtpAccount account, DateTime now) {
    _timeBased(account);
    final periodMs = account.period * 1000;
    return 1 - ((_millis(now) % periodMs) / periodMs);
  }

  /// A code in groups of three digits for reading (PRODUCT-DESCRIPTION.md, "The code list"): `123 456`, `123 456 7`, `123 456 78`.
  static String group(String code) {
    final sb = StringBuffer();
    for (var i = 0; i < code.length; i += 3) {
      if (i > 0) sb.write(' ');
      sb.write(code.substring(i, i + 3 > code.length ? code.length : i + 3));
    }
    return sb.toString();
  }
}
