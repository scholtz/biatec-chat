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
    if (counter < 0) throw ArgumentError('The counter cannot be negative.');
    if (digits < OtpLimits.minDigits || digits > OtpLimits.maxDigits) {
      throw ArgumentError('Digits must be 6 to 8.');
    }
    // The counter is 8 bytes, big endian.
    final message = Uint8List(8);
    ByteData.view(message.buffer).setUint64(0, counter);
    final mac = Hmac(_hash(algorithm), secret).convert(message).bytes;
    final offset = mac[mac.length - 1] & 0x0F;
    final binary = ((mac[offset] & 0x7F) << 24) |
        (mac[offset + 1] << 16) |
        (mac[offset + 2] << 8) |
        mac[offset + 3];
    var mod = 1;
    for (var i = 0; i < digits; i++) {
      mod *= 10;
    }
    return (binary % mod).toString().padLeft(digits, '0');
  }

  static void _timeBased(OtpAccount account) {
    if (account.type != OtpType.totp) {
      throw StateError(
          'A counter-based account has no countdown or next code.');
    }
  }

  static void _usable(OtpAccount account) {
    if (account.isWiped) {
      throw StateError('This account was wiped from memory (the app locked).');
    }
  }

  /// Seconds since the epoch, never below 0 (a reset clock), so the code, the seconds left and the ring agree.
  static int _seconds(DateTime now) {
    final s = now.toUtc().millisecondsSinceEpoch ~/ 1000;
    return s < 0 ? 0 : s;
  }

  /// The step a time-based account is in at [now]: seconds since the epoch divided by the period.
  static int step(OtpAccount account, DateTime now) {
    // A clock before 1970 (a reset real-time clock) counts as 1970 instead of failing in the middle of drawing the list; the clock check in the app tells the user.
    return _seconds(now) ~/ account.period;
  }

  /// The code of [account] at [now]. For a counter-based account that is the code for its current counter, whatever the time.
  static String code(OtpAccount account, DateTime now) => _codeFor(account,
      account.type == OtpType.hotp ? account.counter : step(account, now));

  /// The code of the following period (a time-based account), to show in the last seconds of this one. A counter-based account has no next code to show: its next code is only for after the current one was used.
  static String nextCode(OtpAccount account, DateTime now) {
    _timeBased(account);
    return _codeFor(account, step(account, now) + 1);
  }

  static String _codeFor(OtpAccount account, int value) {
    _usable(account);
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
    var ms = now.toUtc().millisecondsSinceEpoch;
    if (ms < 0) ms = 0;
    final periodMs = account.period * 1000;
    return 1 - ((ms % periodMs) / periodMs);
  }

  /// A code split for reading: `123 456`, `1234 5678`, `123 4567`.
  static String group(String code) {
    final split = code.length == 8 ? 4 : 3;
    if (code.length <= split) return code;
    return '${code.substring(0, split)} ${code.substring(split)}';
  }
}
