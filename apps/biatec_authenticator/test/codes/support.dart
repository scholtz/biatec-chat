import 'dart:async';

import 'package:flutter_test/flutter_test.dart' show addTearDown;

import 'package:biatec_authenticator/codes/codes_services.dart';
import 'package:biatec_authenticator/codes/sensitive_clipboard.dart';
import 'package:biatec_authenticator/codes/server_clock.dart';
import 'package:biatec_authenticator/lock/lock_controller.dart';
import 'package:biatec_authenticator/totp/core/otp_account.dart';
import 'package:biatec_authenticator/totp/vault/totp_vault.dart';

import '../lock/support.dart';
import '../totp/vault/support.dart';

class FakeClipboard implements SensitiveClipboard {
  final List<String> copied = [];

  @override
  Future<void> copy(String code) async => copied.add(code);
}

class MemoryCodesPrefs implements CodesPrefs {
  bool reveal = false;
  DateTime? seen;

  @override
  Future<bool> readTapToReveal() async => reveal;

  @override
  Future<void> writeTapToReveal(bool value) async => reveal = value;

  @override
  Future<DateTime?> readLatestSeen() async => seen;

  @override
  Future<void> writeLatestSeen(DateTime value) async => seen = value;
}

/// The Codes tab on in-memory parts: a vault with a real AES-GCM fake key store, a fake clipboard, a clock you move.
class CodesRig {
  CodesRig._(this.lock, this.auth, this.now);

  static Future<CodesRig> create({DateTime? now, LockController? lock}) async {
    final auth = FakeAuthenticator();
    if (lock == null) {
      lock = LockController(authenticator: auth, store: MemoryLockStore());
      addTearDown(lock.dispose);
      await lock.unlock();
    }
    return CodesRig._(lock, auth, now ?? DateTime.utc(2026, 11, 2, 10, 0, 0));
  }

  final LockController lock;

  /// The fake fingerprint of [lock] (when the rig made the lock).
  final FakeAuthenticator auth;
  DateTime now;
  final keys = FakeVaultKeyStore();
  final storage = MemoryVaultStorage();
  final versions = MemoryVersionStore();
  final clipboard = FakeClipboard();
  final prefs = MemoryCodesPrefs();
  final serverClock = ServerClock();

  late final TotpVault vault =
      TotpVault(keys: keys, storage: storage, versions: versions);

  late final CodesServices services = CodesServices(
    vault: vault,
    lock: lock,
    clipboard: clipboard,
    serverClock: serverClock,
    clock: () => now,
    prefs: prefs,
  );

  void advance(Duration d) => now = now.add(d);

  Future<void> addAccount(OtpAccount account) async {
    // The test keeps its own account to compare with; the vault sealed a copy.
    await vault.add(account);
  }
}

/// An unlocked lock and the Codes services that share it, for tests that build the gate or the shell themselves.
Future<({LockController lock, CodesServices codes})> appParts() async {
  final rig = await CodesRig.create();
  return (lock: rig.lock, codes: rig.services);
}
