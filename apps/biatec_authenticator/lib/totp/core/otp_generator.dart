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

  /// The step a time-based account is in at [now]: seconds since the epoch divided by the period.
  static int step(OtpAccount account, DateTime now) {
    final seconds = now.toUtc().millisecondsSinceEpoch ~/ 1000;
    // A clock before 1970 (a reset real-time clock) gives step 0 instead of an error in the middle of drawing the list; the clock check in the app tells the user.
    if (seconds < 0) return 0;
    return seconds ~/ account.period;
  }

  /// The code of [account] at [now]. For a counter-based account that is the code for its current counter, whatever the time.
  static String code(OtpAccount account, DateTime now) {
    final value =
        account.type == OtpType.hotp ? account.counter : step(account, now);
    return hotp(account.secret, value,
        algorithm: account.algorithm, digits: account.digits);
  }

  /// The code of the following period (a time-based account), to show in the last seconds of this one.
  static String nextCode(OtpAccount account, DateTime now) {
    if (account.type == OtpType.hotp) {
      return hotp(account.secret, account.counter + 1,
          algorithm: account.algorithm, digits: account.digits);
    }
    return hotp(account.secret, step(account, now) + 1,
        algorithm: account.algorithm, digits: account.digits);
  }

  /// Whole seconds left until the code changes, 1 to [OtpAccount.period] (a time-based account).
  static int secondsLeft(OtpAccount account, DateTime now) {
    final seconds = now.toUtc().millisecondsSinceEpoch ~/ 1000;
    return account.period - (seconds % account.period);
  }

  /// A fraction from 1 (just started) to 0 (about to change), for a countdown ring.
  static double fractionLeft(OtpAccount account, DateTime now) {
    final ms = now.toUtc().millisecondsSinceEpoch;
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
