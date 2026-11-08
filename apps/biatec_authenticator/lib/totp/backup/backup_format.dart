import 'dart:convert';
import 'dart:isolate';
import 'dart:math';
import 'dart:typed_data';

import 'package:flutter/foundation.dart' show visibleForTesting;
import 'package:pointycastle/export.dart';

import '../core/base32.dart';
import '../core/otp_account.dart';
import 'argon2.dart';

/// Every way a backup can fail to open gives this one failure and this one message: a wrong passphrase, a changed bit, a cut file, a file made with weaker settings. Telling them apart would tell an attacker which guess was close.
class BackupFailure implements Exception {
  const BackupFailure();

  static const message =
      'This backup could not be opened. Check the passphrase and the file.';

  @override
  String toString() => message;
}

/// What the backup asks of its key derivation, and what a file may ask for.
///
/// [floor] is the weakest Argon2id the app writes or accepts (the spec's 64 MiB, 3 passes, 4 lanes); a file below it is refused, and the app never writes below it, however slow the phone. The ceilings stop a file from asking for so much memory or time that opening it takes the app down.
class BackupPolicy {
  const BackupPolicy({
    this.floor =
        const Argon2idParams(memoryKiB: 64 * 1024, iterations: 3, lanes: 4),
    this.maxMemoryKiB = 256 * 1024,
    this.maxIterations = 12,
    this.maxLanes = 16,
    this.maxFileBytes = 4 * 1024 * 1024,
    this.maxAccounts = 1000,
  });

  final Argon2idParams floor;
  final int maxMemoryKiB;
  final int maxIterations;
  final int maxLanes;
  final int maxFileBytes;
  final int maxAccounts;

  bool accepts(Argon2idParams p) =>
      p.memoryKiB >= floor.memoryKiB &&
      p.iterations >= floor.iterations &&
      p.lanes >= floor.lanes &&
      p.memoryKiB <= maxMemoryKiB &&
      p.iterations <= maxIterations &&
      p.lanes <= maxLanes &&
      // Argon2 needs at least 8 KiB per lane.
      p.memoryKiB >= 8 * p.lanes;
}

/// Runs the key derivation. The default does it in another isolate so the screen keeps moving; tests may run it inline.
typedef KdfRunner = Future<Uint8List> Function(
    Uint8List password, Uint8List salt, Argon2idParams params);

Future<Uint8List> isolateKdf(
        Uint8List password, Uint8List salt, Argon2idParams params) =>
    Isolate.run(() => argon2id(password, salt, params));

/// The backup file.
///
/// ```
/// "BTBK" | version 1 | kdf 1 (Argon2id 1.3) | memory KiB u32 | iterations u32 | lanes u8 | salt 16 | nonce 12 | AES-256-GCM(JSON)+tag
/// ```
///
/// The whole header (everything before the ciphertext) is the associated data of the encryption, so changing any byte of it, the parameters included, makes the file fail. The key is Argon2id of the passphrase and a random salt; the nonce is random per file. The JSON holds the accounts and is padded with spaces to a multiple of 4 KiB so the size says little about how many there are. The file name carries no account names (see [fileName]).
class BackupCodec {
  BackupCodec({
    this.policy = const BackupPolicy(),
    Random? random,
    KdfRunner? kdf,
  })  : _random = random ?? Random.secure(),
        _kdf = kdf ?? isolateKdf;

  final BackupPolicy policy;
  final Random _random;
  final KdfRunner _kdf;

  static const magic = [0x42, 0x54, 0x42, 0x4b]; // BTBK
  static const version = 1;
  static const kdfArgon2id = 1;
  static const headerLength = 4 + 1 + 1 + 4 + 4 + 1 + 16 + 12;
  static const _pad = 4096;
  static const _tag = 16;

  /// A file name that says what it is and when, and nothing about what is in it.
  static String fileName(DateTime now) {
    String two(int n) => n.toString().padLeft(2, '0');
    return 'biatec-authenticator-backup-${now.year}${two(now.month)}${two(now.day)}.biatecbackup';
  }

  Uint8List _bytes(int n) =>
      Uint8List.fromList(List<int>.generate(n, (_) => _random.nextInt(256)));

  Uint8List _header(Argon2idParams p, Uint8List salt, Uint8List nonce) {
    final b = ByteData(headerLength);
    for (var i = 0; i < 4; i++) {
      b.setUint8(i, magic[i]);
    }
    b.setUint8(4, version);
    b.setUint8(5, kdfArgon2id);
    b.setUint32(6, p.memoryKiB);
    b.setUint32(10, p.iterations);
    b.setUint8(14, p.lanes);
    final out = b.buffer.asUint8List();
    out.setRange(15, 31, salt);
    out.setRange(31, 43, nonce);
    return out;
  }

  Uint8List _crypt(bool encrypt, Uint8List key, Uint8List nonce, Uint8List aad,
      Uint8List input) {
    final cipher = GCMBlockCipher(AESEngine())
      ..init(encrypt, AEADParameters(KeyParameter(key), 128, nonce, aad));
    return cipher.process(input);
  }

  static void _wipe(Uint8List b) => b.fillRange(0, b.length, 0);

  /// Seals [accounts] under [passphrase] with the floor parameters (never less). Returns the file.
  Future<Uint8List> encrypt(List<OtpAccount> accounts, String passphrase) =>
      sealJson({
        'v': 1,
        'accounts': [for (final a in accounts) _toJson(a)],
      }, passphrase);

  /// Seals any JSON document in the file format. Production code goes through [encrypt]; tests use this to make a file whose content is not what the app would write (to see that opening it is refused).
  @visibleForTesting
  Future<Uint8List> sealJson(Object json, String passphrase,
      {Argon2idParams? params}) async {
    params ??= policy.floor;
    final salt = _bytes(16);
    final nonce = _bytes(12);
    final header = _header(params, salt, nonce);
    final password = Uint8List.fromList(utf8.encode(passphrase));
    final Uint8List key;
    try {
      key = await _kdf(password, salt, params);
    } finally {
      _wipe(password);
    }
    final bytes = utf8.encode(jsonEncode(json));
    final size = ((bytes.length + _pad - 1) ~/ _pad) * _pad;
    final padded = Uint8List(size)
      ..fillRange(0, size, 0x20)
      ..setRange(0, bytes.length, bytes);
    try {
      final sealed = _crypt(true, key, nonce, header, padded);
      return Uint8List.fromList([...header, ...sealed]);
    } finally {
      _wipe(key);
      _wipe(padded);
    }
  }

  Map<String, Object?> _toJson(OtpAccount a) {
    final secret = a.copySecret();
    try {
      return {
        'issuer': a.issuer,
        'label': a.label,
        'secret': Base32.encode(secret),
        'algorithm': a.algorithm.uriName,
        'digits': a.digits,
        'period': a.period,
        'type': a.type.name,
        'counter': a.counter,
      };
    } finally {
      _wipe(secret);
    }
  }

  /// Opens [file] with [passphrase]. Throws [BackupFailure], the same one for every cause. The accounts returned are the caller's to wipe.
  Future<List<OtpAccount>> decrypt(Uint8List file, String passphrase) async {
    final password = Uint8List.fromList(utf8.encode(passphrase));
    try {
      return await _decrypt(file, password);
    } on BackupFailure {
      rethrow;
    } on Object {
      throw const BackupFailure();
    } finally {
      _wipe(password);
    }
  }

  Future<List<OtpAccount>> _decrypt(Uint8List file, Uint8List password) async {
    Argon2idParams? params;
    Uint8List? salt;
    Uint8List? nonce;
    final plausible = file.length >= headerLength + _tag &&
        file.length <= policy.maxFileBytes &&
        _magicOk(file);
    if (plausible) {
      final b = ByteData.sublistView(file);
      if (b.getUint8(4) == version && b.getUint8(5) == kdfArgon2id) {
        final p = Argon2idParams(
            memoryKiB: b.getUint32(6),
            iterations: b.getUint32(10),
            lanes: b.getUint8(14));
        if (policy.accepts(p)) {
          params = p;
          salt = Uint8List.sublistView(file, 15, 31);
          nonce = Uint8List.sublistView(file, 31, 43);
        }
      }
    }
    if (params == null || salt == null || nonce == null) {
      // Whatever is wrong with the file, the time spent is that of a real attempt, so the answer is not faster for a refused file.
      await _kdf(password, Uint8List(16), policy.floor).then(_wipe);
      throw const BackupFailure();
    }
    final key = await _kdf(password, Uint8List.fromList(salt), params);
    final Uint8List plain;
    try {
      plain = _crypt(
          false,
          key,
          Uint8List.fromList(nonce),
          Uint8List.sublistView(file, 0, headerLength),
          Uint8List.sublistView(file, headerLength));
    } finally {
      _wipe(key);
    }
    try {
      return _parse(plain);
    } finally {
      _wipe(plain);
    }
  }

  bool _magicOk(Uint8List f) {
    for (var i = 0; i < 4; i++) {
      if (f[i] != magic[i]) return false;
    }
    return true;
  }

  List<OtpAccount> _parse(Uint8List plain) {
    final decoded = jsonDecode(utf8.decode(plain));
    if (decoded is! Map || decoded['v'] != 1) throw const BackupFailure();
    final list = decoded['accounts'];
    if (list is! List || list.length > policy.maxAccounts) {
      throw const BackupFailure();
    }
    final out = <OtpAccount>[];
    try {
      for (final raw in list) {
        if (raw is! Map) throw const BackupFailure();
        final algorithm = OtpAlgorithm.fromUriName(raw['algorithm'] as String);
        final type = OtpType.values.byName(raw['type'] as String);
        if (algorithm == null) throw const BackupFailure();
        final secret = Base32.decode(raw['secret'] as String);
        try {
          // The same validation as a scan: a poisoned file gets no further than a bad QR code would.
          out.add(OtpAccount(
            issuer: raw['issuer'] as String,
            label: raw['label'] as String,
            secret: secret,
            algorithm: algorithm,
            digits: raw['digits'] as int,
            period: raw['period'] as int,
            type: type,
            counter: raw['counter'] as int,
          ));
        } finally {
          _wipe(secret);
        }
      }
    } on Object {
      for (final a in out) {
        a.wipe();
      }
      throw const BackupFailure();
    }
    return out;
  }
}
