import 'dart:typed_data';

/// A string that is not valid Base32. The message never contains the input, which may be a secret.
class Base32Exception implements Exception {
  const Base32Exception(this.message);
  final String message;

  @override
  String toString() => 'Base32Exception: $message';
}

/// Base32 (RFC 4648 section 6), the encoding of one-time password secrets.
abstract final class Base32 {
  static const _alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

  static final Map<int, int> _values = {
    for (var i = 0; i < _alphabet.length; i++) _alphabet.codeUnitAt(i): i,
  };

  /// Decodes [input]. Case, spaces, dashes and missing `=` padding are tolerated, because people copy setup keys from web pages in groups of four. Anything else outside the alphabet is refused.
  static Uint8List decode(String input) {
    final units = <int>[];
    for (final u in input.codeUnits) {
      // Whitespace and dashes group the key for reading; they carry nothing.
      if (u == 0x20 || u == 0x09 || u == 0x0A || u == 0x0D || u == 0x2D) {
        continue;
      }
      units.add(u);
    }
    // Padding is only allowed at the end.
    var end = units.length;
    while (end > 0 && units[end - 1] == 0x3D) {
      end--;
    }
    if (end == 0) throw const Base32Exception('The key is empty.');
    final out = <int>[];
    var buffer = 0;
    var bits = 0;
    for (var i = 0; i < end; i++) {
      var u = units[i];
      if (u >= 0x61 && u <= 0x7A) u -= 0x20; // lower case
      final v = _values[u];
      if (v == null) {
        throw const Base32Exception('The key has a character that is not '
            'part of Base32 (letters A to Z and digits 2 to 7).');
      }
      buffer = (buffer << 5) | v;
      bits += 5;
      if (bits >= 8) {
        bits -= 8;
        out.add((buffer >> bits) & 0xFF);
        buffer &= (1 << bits) - 1;
      }
    }
    // A last group of 1, 3 or 6 characters cannot come from whole bytes, and
    // leftover bits must be zero in a canonical encoding.
    final rest = end % 8;
    if (rest == 1 || rest == 3 || rest == 6) {
      throw const Base32Exception('The key has the wrong length.');
    }
    if (buffer != 0) {
      throw const Base32Exception('The key is not valid Base32.');
    }
    return Uint8List.fromList(out);
  }

  /// Encodes [bytes] in upper case without padding, as services show setup keys.
  static String encode(List<int> bytes) {
    final sb = StringBuffer();
    var buffer = 0;
    var bits = 0;
    for (final b in bytes) {
      buffer = (buffer << 8) | (b & 0xFF);
      bits += 8;
      while (bits >= 5) {
        bits -= 5;
        sb.write(_alphabet[(buffer >> bits) & 0x1F]);
      }
      buffer &= (1 << bits) - 1;
    }
    if (bits > 0) sb.write(_alphabet[(buffer << (5 - bits)) & 0x1F]);
    return sb.toString();
  }
}
