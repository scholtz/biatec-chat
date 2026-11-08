import 'dart:math';

/// How good a passphrase looks.
enum PassphraseStrength { refused, fair, good, strong }

/// The verdict on a passphrase: [problem] says in words why it is refused (null when it is accepted), [bits] is a rough estimate of the guesses it takes, and [strength] is that in a word.
class PassphraseVerdict {
  const PassphraseVerdict(this.strength, this.bits, this.problem);

  final PassphraseStrength strength;
  final double bits;
  final String? problem;

  bool get accepted => problem == null;
}

/// Rules and a strength estimate for the backup passphrase. The estimate is deliberately cautious: it counts the kinds of characters used, then takes away what an attacker would try first (a repeated character, a run like `abcd` or `1234`, a common password or keyboard walk inside it). It is a screen for the weakest choices, not a promise: the passphrase is the only thing between a stolen file and every seed, so the app also says that a lost passphrase cannot be recovered and that a long phrase of several words is best.
abstract final class Passphrase {
  static const minLength = 12;

  /// Below this many estimated bits the passphrase is refused.
  static const minBits = 50.0;

  static const _common = [
    'password',
    'passw0rd',
    'letmein',
    'welcome',
    'qwerty',
    'qwertz',
    'azerty',
    'asdfgh',
    'zxcvbn',
    '123456',
    'abcdef',
    'iloveyou',
    'monkey',
    'dragon',
    'football',
    'baseball',
    'master',
    'login',
    'admin',
    'biatec',
    'authenticator',
    'backup',
    'secret',
    'changeme',
    'trustno1',
    'sunshine',
    'princess',
    'starwars',
    'whatever',
    'freedom',
  ];

  /// The size of the set of characters the passphrase draws from.
  static int _pool(String s) {
    var pool = 0;
    if (RegExp(r'[a-z]').hasMatch(s)) pool += 26;
    if (RegExp(r'[A-Z]').hasMatch(s)) pool += 26;
    if (RegExp(r'[0-9]').hasMatch(s)) pool += 10;
    if (RegExp(r'[ ]').hasMatch(s)) pool += 1;
    if (RegExp(r'[!-/:-@\[-`{-~]').hasMatch(s)) pool += 32;
    if (s.runes.any((r) => r > 126)) pool += 100;
    return max(pool, 1);
  }

  /// How many characters count: repeats and runs and common words count for little.
  static double _effectiveLength(String s) {
    final lower = s.toLowerCase();
    final runes = lower.runes.toList();
    final counted = List<bool>.filled(runes.length, true);
    // Common passwords and walks inside it do not count.
    // (Searched by character, not by UTF-16 unit, so an emoji before a word cannot shift the positions.)
    for (final c in _common) {
      final word = c.runes.toList();
      for (var at = 0; at + word.length <= runes.length; at++) {
        var match = true;
        for (var j = 0; j < word.length && match; j++) {
          match = runes[at + j] == word[j];
        }
        if (match) {
          for (var i = at; i < at + word.length; i++) {
            counted[i] = false;
          }
        }
      }
    }
    // A character equal to the previous one, or one more or less than it (a run), adds nothing new.
    for (var i = 1; i < runes.length; i++) {
      final d = runes[i] - runes[i - 1];
      if (d == 0 || d == 1 || d == -1) counted[i] = false;
    }
    // Only as many different characters as there are distinct ones can add surprise: abcabcabcabc is three.
    final distinct = runes.toSet().length;
    var n = min(counted.where((c) => c).length.toDouble(), distinct * 2.0);
    // A phrase that is one block repeated is as strong as the block.
    for (var p = 1; p <= runes.length ~/ 2; p++) {
      var repeats = true;
      for (var i = p; i < runes.length && repeats; i++) {
        repeats = runes[i] == runes[i - p];
      }
      if (repeats) {
        n = min(n, p.toDouble());
        break;
      }
    }
    return n;
  }

  /// The verdict for [value], and for [confirmation] if one is given (it must match).
  static PassphraseVerdict check(String value, {String? confirmation}) {
    final length = value.runes.length;
    if (length < minLength) {
      return PassphraseVerdict(PassphraseStrength.refused, 0,
          'Use at least $minLength characters. A phrase of several words is easy to remember and hard to guess.');
    }
    final bits = _effectiveLength(value) * (log(_pool(value)) / ln2);
    if (bits < minBits) {
      return PassphraseVerdict(PassphraseStrength.refused, bits,
          'This is too easy to guess: it repeats, runs on, or is built from a common word. Try several unrelated words, or a longer phrase.');
    }
    if (confirmation != null && confirmation != value) {
      return PassphraseVerdict(
          PassphraseStrength.fair, bits, 'The two entries are not the same.');
    }
    final strength = bits >= 100
        ? PassphraseStrength.strong
        : bits >= 70
            ? PassphraseStrength.good
            : PassphraseStrength.fair;
    return PassphraseVerdict(strength, bits, null);
  }
}
