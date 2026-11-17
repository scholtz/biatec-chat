// BCHAT-032: the key store reads where the native keystore says the key lives, and a missing or unknown value is never read as hardware.
import 'dart:io';

import 'package:biatec_authenticator/keys/device_key_store.dart';
import 'package:biatec_authenticator/keys/key_security_level.dart';
import 'package:biatec_authenticator/security/pin_prompt.dart';
import 'package:flutter/foundation.dart' show TargetPlatform, debugDefaultTargetPlatformOverride;
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  const channel = MethodChannel('io.biatec.authenticator/keystore');
  final messenger =
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;

  void answer(Object? Function(MethodCall call) handler) => messenger
      .setMockMethodCallHandler(channel, (call) async => handler(call));

  tearDown(() => messenger.setMockMethodCallHandler(channel, null));

  group('KeySecurityLevel', () {
    test('hardware levels are hardware; software, the Windows account and unknown are not', () {
      for (final level in KeySecurityLevel.values) {
        expect(
          level.isHardware,
          level != KeySecurityLevel.software &&
              level != KeySecurityLevel.windowsAccount &&
              level != KeySecurityLevel.unknown,
          reason: level.wire,
        );
      }
    });

    test('a key of the Windows account may be enrolled and signs; software and unknown may not be enrolled', () {
      for (final level in KeySecurityLevel.values) {
        expect(level.isEnrollable, level != KeySecurityLevel.software && level != KeySecurityLevel.unknown, reason: level.wire);
      }
      expect(KeySecurityLevel.fromWire('windows_account'), KeySecurityLevel.windowsAccount);
      expect(KeySecurityLevel.windowsAccount.isHardware, isFalse, reason: 'never shown as hardware');
      expect(KeySecurityLevel.windowsAccount.blocksSigning, isFalse);
      expect(KeySecurityLevel.software.blocksSigning, isTrue, reason: 'a phone that reports software is still refused');
    });

    test('the wire values round-trip and anything else is unknown', () {
      for (final level in KeySecurityLevel.values) {
        expect(KeySecurityLevel.fromWire(level.wire), level);
      }
      for (final value in [null, '', 'STRONGBOX', 'hardware-backed', 7, true]) {
        expect(KeySecurityLevel.fromWire(value), KeySecurityLevel.unknown,
            reason: '$value');
      }
    });

    test('every level has a label for the user', () {
      for (final level in KeySecurityLevel.values) {
        expect(level.label, isNotEmpty);
      }
    });
  });

  Map<String, Object?> part(String level, {bool bound = false}) =>
      {'x': 'f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU', 'y': 'x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0', 'securityLevel': level, 'authBound': bound};

  group('PlatformDeviceKeyStore', () {
    const store = PlatformDeviceKeyStore();

    test('generateKeys carries the level and the auth binding of each key',
        () async {
      answer((call) => {
            'approval': part('strongbox', bound: true),
            'presence': part('tee'),
          });
      final keys = await store.generateKeys();
      expect(keys.approval.securityLevel, KeySecurityLevel.strongBox);
      expect(keys.approval.authBound, isTrue);
      expect(keys.presence.securityLevel, KeySecurityLevel.trustedEnvironment);
      expect(keys.presence.authBound, isFalse);
      expect(keys.approval.kid, startsWith('biatec-approve-'));
      expect(keys.presence.kid, startsWith('biatec-presence-'));
      expect(keys.approval.kid, isNot(keys.presence.kid));
    });

    test('only the approval key can be auth bound, and only when the plugin says true',
        () async {
      answer((call) => {
            'approval': part('tee'),
            'presence': part('tee', bound: true),
          });
      final keys = await store.generateKeys();
      expect(keys.approval.authBound, isFalse, reason: 'not reported');
      expect(keys.presence.authBound, isFalse, reason: 'never for presence');
    });

    test('a plugin that does not report the level gives unknown, not hardware',
        () async {
      answer((call) => {
            'approval': {'x': 'xx', 'y': 'yy'},
            'presence': {'x': 'xx', 'y': 'yy'},
          });
      final key = (await store.generateKeys()).approval;
      expect(key.securityLevel, KeySecurityLevel.unknown);
      expect(key.securityLevel.isHardware, isFalse);
    });

    test('load asks for one use and a missing key is null', () async {
      final asked = <Object?>[];
      answer((call) {
        asked.add((call.arguments as Map)['use']);
        return null;
      });
      expect(await store.loadApproval(), isNull);
      expect(await store.loadPresence(), isNull);
      // The CHANNEL vocabulary (approval, presence), not the protocol's key use (approve, presence).
      expect(asked, ['approval', 'presence']);
    });

    test('the exact method names and arguments the Dart side sends', () async {
      final calls = <MethodCall>[];
      answer((call) {
        calls.add(call);
        switch (call.method) {
          case 'generateKeys':
            return {'approval': part('tee', bound: true), 'presence': part('tee')};
          case 'keysState':
            return 'ok';
          case 'signPresence':
          case 'signApproval':
            return Uint8List(64);
        }
        return null;
      });
      await store.generateKeys();
      await store.loadApproval();
      await store.loadPresence();
      await store.keysState();
      await store.signPresence(Uint8List(1));
      await store.signApproval(Uint8List(1), promptTitle: 't', promptSubtitle: 's');
      await store.deleteKeys();
      await store.deleteLegacyKey('old-alias');
      expect([for (final c in calls) '${c.method} ${c.arguments is Map ? ((c.arguments as Map).keys.toList()..sort()).join(',') : ''}'], [
        'generateKeys ',
        'load use',
        'load use',
        'keysState ',
        'signPresence data',
        'signApproval data,subtitle,title',
        'deleteKeys ',
        'delete alias',
      ]);
      expect([for (final c in calls.where((c) => c.method == 'load')) (c.arguments as Map)['use']], ['approval', 'presence']);
    });

    test('KeystorePlugin.kt and KeystorePlugin.swift understand every name and value the Dart side sends', () {
      final kt = File('android/app/src/main/kotlin/io/biatec/authenticator/KeystorePlugin.kt').readAsStringSync();
      final swift = File('ios/Runner/KeystorePlugin.swift').readAsStringSync();
      for (final method in ['generateKeys', 'generatePresence', 'load', 'loadLegacy', 'signLegacy', 'keysState', 'signPresence', 'signApproval', 'deleteKeys', 'delete']) {
        expect(kt, contains('"$method"'), reason: 'Kotlin: $method');
        expect(swift, contains('"$method"'), reason: 'Swift: $method');
      }
      for (final use in [channelUse(KeyUse.approve), channelUse(KeyUse.presence)]) {
        expect(kt, contains('"$use" ->'), reason: 'Kotlin load use $use');
        expect(swift, contains('case "$use"'), reason: 'Swift load use $use');
      }
      for (final arg in ['use', 'data', 'title', 'subtitle', 'alias']) {
        expect(kt, contains('"$arg"'), reason: 'Kotlin argument $arg');
        expect(swift, contains('"$arg"'), reason: 'Swift argument $arg');
      }
      // What the plugins answer: the keys state, the key map and the error codes Dart maps.
      for (final wire in ['ok', 'approval_key_invalidated', 'missing']) {
        expect(KeysState.fromWire(wire).wire, wire);
        expect(kt, contains('"$wire"'));
        expect(swift, contains('"$wire"'));
      }
      // Single-key mode makes ONLY the prompt-free key: no approval key, no biometric requirement, on both platforms.
      final ktGen = kt.substring(kt.indexOf('private fun generatePresence'));
      expect(ktGen.substring(0, ktGen.indexOf('private fun signWith')), isNot(contains('APPROVAL_ALIAS')));
      final swGen = swift.substring(swift.indexOf('private func generatePresence'));
      expect(swGen.substring(0, swGen.indexOf('// One approval prompt at a time')), isNot(contains('approvalTag')));
      // Single-key mode keeps the key usable while the phone is locked (the stay-connected service signs then); the two-key keys need an unlocked phone.
      expect(ktGen.substring(0, ktGen.indexOf('private fun signWith')), contains('unlocked = false'));
      expect(kt, contains('if (unlocked && Build.VERSION.SDK_INT'));
      expect(kt, contains('make(PRESENCE_ALIAS, false)\n'), reason: 'the two-key presence key keeps the default (unlocked required)');
      // keysState does not leave a probe operation open on every call (it is cached).
      expect(kt, contains('STATE_TTL_MS'));
      // One approval prompt at a time, on both platforms.
      expect(kt, contains('promptActive'));
      expect(swift, contains('promptActive'));
      // The old alias of an install from before the two keys is still looked up (and never the new aliases).
      expect(kt, contains('legacyAlias(call)'));
      expect(swift, contains('legacyAlias(args)'));
      for (final code in ['prompt_active', 'cancelled', 'approval_key_invalidated', 'no_biometrics', 'locked_out', 'auth_failed', 'key_generation_failed', 'insecure_key']) {
        expect(kt, contains('"$code"'), reason: 'Kotlin error $code');
        expect(swift, contains('"$code"'), reason: 'Swift error $code');
      }
    });

    test('key_channels.cpp (Windows) understands every name and value the Dart side sends and answers what Dart reads', () {
      // Read without line ends and indentation, so a checkout with CRLF or a reformat does not change what is checked.
      String source(String path) => File(path).readAsStringSync().replaceAll('\r', '');
      String flat(String s) => s.replaceAll(RegExp(r'\s+'), ' ');
      final cpp = source('windows/runner/key_channels.cpp');
      final keystoreCpp = source('windows/runner/keystore.cpp');
      for (final method in ['generateKeys', 'generatePresence', 'load', 'loadLegacy', 'signLegacy', 'keysState', 'signPresence', 'signApproval', 'deleteKeys', 'delete', 'probe']) {
        expect(cpp, contains('"$method"'), reason: 'C++: $method');
      }
      for (final use in [channelUse(KeyUse.approve), channelUse(KeyUse.presence)]) {
        expect(cpp, contains('"$use"'), reason: 'C++ load use $use');
      }
      for (final arg in ['use', 'data', 'title', 'subtitle']) {
        expect(cpp, contains('"$arg"'), reason: 'C++ argument $arg');
      }
      for (final wire in ['ok', 'missing']) {
        expect(cpp, contains('"$wire"'), reason: 'C++ keys state $wire');
      }
      // The gesture is the app's, not the hardware's: the approval key is reported as `appGated`, never as `authBound`.
      expect(flat(cpp), contains('map[Text("authBound")] = EncodableValue(false);'));
      expect(flat(cpp), contains('map[Text("appGated")] = EncodableValue(approval);'));
      // The level it reports is one Dart knows, and as hardware only when the provider says so.
      expect(KeySecurityLevel.fromWire('tpm'), KeySecurityLevel.tpm);
      expect(KeySecurityLevel.tpm.isHardware, isTrue);
      // A key of the software store (a computer whose TPM cannot be used) has its own level, which Dart enrolls with a warning; `tpm` is never said for it.
      expect(cpp, contains('key.hardware ? "tpm" : key.store == keystore::Store::kSoftware ? "windows_account" : "software"'));
      expect(flat(keystoreCpp), contains('key_out.hardware = store == Store::kTpm && InHardware(provider, key);'));
      expect(flat(keystoreCpp), contains('const Store store = TpmAvailable() ? Store::kTpm : Store::kSoftware;'), reason: 'the TPM whenever it can be used');
      expect(cpp, isNot(contains('"no_tpm"')), reason: 'a computer without a TPM is not refused');
      expect(flat(cpp), contains('map[Text("software")] = EncodableValue(true);'));
      // Whether the provider is hardware is asked of the provider: asked of a key, a real TPM answers "not supported", and every key was refused.
      expect(flat(keystoreCpp), contains('if (!DwordProperty(provider, NCRYPT_IMPL_TYPE_PROPERTY, &impl)) return false;'));
      // The PIN of the software store: a salted slow hash, encrypted for the Windows account, with a wait after wrong tries; removed with the keys.
      expect(keystoreCpp, contains('BCryptDeriveKeyPBKDF2'));
      expect(keystoreCpp, contains('constexpr ULONG kPinIterations = 600000;'));
      expect(keystoreCpp, contains('CryptProtectData'));
      expect(flat(keystoreCpp), contains('if (found.store == Store::kSoftware) return CheckKeptPin(pin);'));
      expect(flat(keystoreCpp), contains('if (!ReadPinRecord(&record)) return PinCheck::kRefused;'), reason: 'no record: nothing is accepted');
      expect(flat(keystoreCpp), contains('return PinCheck::kLocked;'));
      expect(flat(keystoreCpp), contains('DeletePinRecord(); if (failure) throw *failure;'));
      expect(keystoreCpp, contains('"The software key can be exported."'), reason: 'a software key that can be exported is refused');
      for (final code in ['prompt_active', 'cancelled', 'key_missing', 'no_biometrics', 'locked_out', 'auth_failed']) {
        expect(cpp, contains('"$code"'), reason: 'C++ error $code');
      }
      expect(keystoreCpp, contains('"key_generation_failed"'));
      expect(keystoreCpp, contains('"insecure_key"'), reason: 'a key not reported as hardware is refused');
      // One operation on the keys at a time; a whole pair or none; an old key that cannot be deleted is an error and is never reused.
      expect(keystoreCpp, contains('std::mutex g_keys'));
      expect(flat(keystoreCpp), contains('auto FreshKeys(Make make) { std::lock_guard<std::mutex> lock(g_keys);'));
      expect(flat(keystoreCpp), contains('return FreshKeys('));
      expect(keystoreCpp, contains('The old key cannot be deleted'));
      expect(flat(cpp), contains('keystore::CreateFreshPair(pin ? &pin->value() : nullptr)'));
      expect(cpp, isNot(contains('"no_hello"')), reason: 'a computer without Windows Hello is not refused: it gets the PIN of the app');
      expect(cpp, isNot(contains('GenerateIfMissing')), reason: 'an old pair is never handed to a new enrollment');
      // Both keys are P-256 keys of the TPM's provider, not exportable (no export policy is ever set), and the approval signature runs only after the Windows Hello
      // check said verified, inside the one native call.
      expect(keystoreCpp, contains('MS_PLATFORM_CRYPTO_PROVIDER'));
      expect(keystoreCpp, contains('BCRYPT_ECDSA_P256_ALGORITHM'));
      expect(keystoreCpp, isNot(contains('NCRYPT_ALLOW_EXPORT_FLAG')));
      expect(keystoreCpp, isNot(contains('NCRYPT_ALLOW_PLAINTEXT')));
      // A source check, not a run: the function that signs an approval asks first, lets only "verified" through, and signs last. (Whitespace and comments are not part of
      // what is compared.)
      final start = cpp.indexOf('EncodableValue SignApproval(');
      expect(start, greaterThan(-1));
      final body = flat(cpp.substring(start, cpp.indexOf('\n}\n', start)).replaceAll(RegExp(r'//[^\n]*'), ''));
      final verify = body.indexOf('hello::Verify(');
      final sign = body.indexOf('keystore::Sign(');
      expect(verify, greaterThan(-1));
      expect(sign, greaterThan(verify), reason: 'the check comes before the signature');
      expect(RegExp('keystore::Sign\\(').allMatches(body).length, 1, reason: 'one signature, behind the check');
      expect(body, contains('case hello::Outcome::kVerified: break;'), reason: 'only "verified" falls through to the signature');
      // From the check to the `return` that carries the signature: nothing in between leaves the function.
      final between = body.substring(verify, body.lastIndexOf('return', sign));
      for (final outcome in ['kCanceled', 'kRetriesExhausted', 'kNotConfigured', 'kNoDevice']) {
        expect(between, contains('case hello::Outcome::$outcome:'), reason: outcome);
      }
      expect(between, contains('default: throw'), reason: 'any other answer signs nothing');
      expect(between, isNot(contains('return')), reason: 'no way out of the function between the check and the signature');

      // Without Windows Hello: the PIN of the app. It is needed only when Windows Hello cannot be used, and nothing is made without it: the answer is
      // `pin_required`, and the app's own screen asks the user and calls again.
      final fresh = flat(cpp.substring(cpp.indexOf('std::optional<Pin> NewPinUnlessHello('), cpp.indexOf('bool PinMode()')));
      expect(fresh, contains('if (availability == hello::Availability::kAvailable) return std::nullopt;'));
      expect(fresh, contains('if (sent == nullptr) { throw keystore::KeyError("pin_required"'));
      expect(fresh, contains('pin.value().size() < kMinPinLength || pin.value().size() > kMaxPinLength'), reason: 'the native side holds the limits too');
      expect(flat(cpp), contains('constexpr size_t kMinPinLength = $pinMinLength;'));
      expect(flat(cpp), contains('constexpr size_t kMaxPinLength = $pinMaxLength;'));
      expect(cpp, isNot(contains('KeyError("approval_key_invalidated"')), reason: 'no change of the biometrics invalidates a TPM key: a gone key is key_missing');
      expect(keystoreCpp, contains('NCRYPT_OVERWRITE_KEY_FLAG'), reason: 'a key the TPM can no longer open does not block a new enrollment');
      // The TPM checks the PIN (a signature with the lock key), and only an accepted one gets past: the approval key then signs with it.
      final require = flat(cpp.substring(cpp.indexOf('Pin RequirePin('), cpp.indexOf('EncodableValue SignApprovalWithPin(')));
      expect(require, contains('if (sent == nullptr) { throw keystore::KeyError("pin_required"'));
      expect(require, contains('case keystore::PinCheck::kAccepted: break;'));
      expect(require, contains('case keystore::PinCheck::kLocked: throw keystore::KeyError("locked_out"'));
      expect(require, contains('default: throw keystore::KeyError("auth_failed"'));
      final withPin = flat(cpp.substring(cpp.indexOf('EncodableValue SignApprovalWithPin('), start));
      expect(withPin.indexOf('RequirePin('), greaterThan(-1));
      expect(withPin.indexOf('RequirePin('), lessThan(withPin.indexOf('keystore::Sign(keystore::Use::kApproval, data, &pin.value())')));
      expect(body, contains('if (existing.lock) return SignApprovalWithPin(data, sent_pin);'));
      expect(body.indexOf('if (existing.lock)'), lessThan(verify), reason: 'a PIN enrollment never reaches the Windows Hello path');
      // The app lock of a PIN enrollment asks the same PIN, and only an accepted one is a success.
      final unlock = flat(cpp.substring(cpp.indexOf('EncodableValue BiometricAuthenticate('), cpp.indexOf('KeyChannels::KeyChannels(')));
      expect(unlock, contains('if (sent_pin == nullptr) return Text("pin_required"); try { RequirePin(sent_pin); return Text("success"); }'));
      // The PIN is the usage secret of the TPM keys: set before the key is finalized, given again for a signature, and kept nowhere (the native copy is wiped).
      expect(flat(keystoreCpp), contains('if (pin != nullptr) { status = SetPin(key, *pin); if (status != ERROR_SUCCESS) return status; } return NCryptFinalizeKey(key.get(), NCRYPT_SILENT_FLAG);'));
      expect(keystoreCpp, contains('NCRYPT_PIN_PROPERTY'));
      expect(flat(keystoreCpp), contains('pair.approval = CreateAndDescribe(store, provider, Use::kApproval, pin);'));
      expect(flat(keystoreCpp), contains('if (pin != nullptr) CreateLock(store, provider, *pin);'));
      expect(flat(keystoreCpp), contains('Create(provider, use, store == Store::kTpm ? pin : nullptr);'), reason: 'only the TPM takes the PIN as a usage secret');
      expect(flat(keystoreCpp), contains('for (const Use use : {Use::kApproval, Use::kPresence, Use::kLock})'), reason: 'a new enrollment and an erase remove the lock key too');
      expect(cpp, contains('SecureZeroMemory'));
      expect(cpp, contains('"pin"'), reason: 'the argument the Dart side sends');
    });

    group('the PIN of the app (a Windows computer without Windows Hello)', () {
      tearDown(() => pinPromptHandler = null);

      test('generateKeys and generatePresence ask for a new PIN after pin_required and call again with it', () async {
        for (final method in ['generateKeys', 'generatePresence']) {
          final calls = <MethodCall>[];
          final asked = <bool>[];
          pinPromptHandler = ({required create, required reason}) async {
            asked.add(create);
            return '246810';
          };
          answer((call) {
            calls.add(call);
            if (call.arguments == null) throw PlatformException(code: 'pin_required');
            return method == 'generateKeys' ? {'approval': part('tpm'), 'presence': part('tpm')} : part('tpm');
          });
          if (method == 'generateKeys') {
            await store.generateKeys();
          } else {
            await store.generatePresenceKey();
          }
          expect(calls.map((c) => c.method), [method, method]);
          expect(calls.last.arguments, {'pin': '246810'});
          expect(asked, [true], reason: 'a new PIN, asked once');
        }
      });

      test('no PIN chosen is cancelled, and the keys are not asked for again', () async {
        var calls = 0;
        pinPromptHandler = ({required create, required reason}) async => null;
        answer((call) {
          calls++;
          throw PlatformException(code: 'pin_required');
        });
        await expectLater(store.generateKeys(), throwsA(isA<PlatformException>().having((e) => e.code, 'code', 'cancelled')));
        expect(calls, 1);
        // Nobody to ask (no screen): the same.
        pinPromptHandler = null;
        await expectLater(store.generatePresenceKey(), throwsA(isA<PlatformException>().having((e) => e.code, 'code', 'cancelled')));
      });

      test('signApproval asks for the PIN after pin_required, with what is approved, and signs with it', () async {
        final calls = <MethodCall>[];
        String? shown;
        pinPromptHandler = ({required create, required reason}) async {
          expect(create, isFalse);
          shown = reason;
          return '246810';
        };
        answer((call) {
          calls.add(call);
          if ((call.arguments as Map)['pin'] == null) throw PlatformException(code: 'pin_required');
          return Uint8List.fromList([7]);
        });
        expect(await store.signApproval(Uint8List.fromList([1]), promptTitle: 'Approve', promptSubtitle: 'EUR 5 to Bob'), [7]);
        expect(shown, 'Approve\nEUR 5 to Bob');
        expect(calls, hasLength(2));
        expect((calls.first.arguments as Map).containsKey('pin'), isFalse);
        expect((calls.last.arguments as Map)['pin'], '246810');
        expect((calls.last.arguments as Map)['data'], [1]);
      });

      test('signApproval: a closed PIN dialog is a cancel, and a PIN the TPM refuses is a typed failure', () async {
        pinPromptHandler = ({required create, required reason}) async => null;
        answer((call) => throw PlatformException(code: 'pin_required'));
        await expectLater(store.signApproval(Uint8List(1), promptTitle: 'a', promptSubtitle: 'b'), throwsA(isA<KeyPromptCancelled>()));
        pinPromptHandler = ({required create, required reason}) async => 'wrong-pin';
        for (final code in ['auth_failed', 'locked_out']) {
          answer((call) => throw PlatformException(code: (call.arguments as Map)['pin'] == null ? 'pin_required' : code));
          await expectLater(store.signApproval(Uint8List(1), promptTitle: 'a', promptSubtitle: 'b'),
              throwsA(isA<KeyPromptFailed>().having((e) => e.code, 'code', code)));
        }
      });
    });

    test('status reads the probe of a Windows build: ready with a TPM or with the software key store, and only with neither a refusal', () async {
      for (final (answerOf, expected) in [
        ({'tpm': false, 'software': true}, KeyStoreStatus.ready),
        ({'software': true}, KeyStoreStatus.ready),
        ({'tpm': false, 'software': false}, KeyStoreStatus.noTpm),
        ({'tpm': false, 'software': 'yes'}, KeyStoreStatus.noTpm),
        ({'tpm': true, 'software': true}, KeyStoreStatus.ready),
        ({'tpm': true, 'hello': 'available'}, KeyStoreStatus.ready),
        ({'tpm': false, 'hello': 'available'}, KeyStoreStatus.noTpm),
        ({'tpm': true, 'hello': 'not_configured'}, KeyStoreStatus.ready),
        ({'tpm': true, 'hello': 'no_device'}, KeyStoreStatus.ready),
        ({'tpm': true, 'hello': 'something new'}, KeyStoreStatus.ready),
        ({'tpm': true, 'hello': 'unavailable'}, KeyStoreStatus.ready),
        ({'tpm': true}, KeyStoreStatus.ready),
        ({'hello': 'available'}, KeyStoreStatus.noTpm),
        ({'tpm': 'yes', 'hello': 'available'}, KeyStoreStatus.noTpm),
      ]) {
        answer((call) => call.method == 'probe' ? answerOf : null);
        expect(await store.status(), expected, reason: '$answerOf');
      }
    });

    test('a Windows approval key is app gated, not auth bound, and either way it counts as gated; a phone cannot claim it', () async {
      debugDefaultTargetPlatformOverride = TargetPlatform.windows;
      addTearDown(() => debugDefaultTargetPlatformOverride = null);
      answer((call) => {
            'approval': {...part('tpm'), 'appGated': true},
            'presence': {...part('tpm'), 'appGated': true},
          });
      final keys = await store.generateKeys();
      expect(keys.approval.securityLevel, KeySecurityLevel.tpm);
      expect(keys.approval.authBound, isFalse);
      expect(keys.approval.appGated, isTrue);
      expect(keys.approval.gestureRequired, isTrue);
      expect(keys.presence.appGated, isFalse, reason: 'never for presence');
      expect(keys.presence.gestureRequired, isFalse);
      answer((call) => {'approval': part('tpm'), 'presence': part('tpm')});
      expect((await store.generateKeys()).approval.gestureRequired, isFalse, reason: 'neither guarantee reported');
      debugDefaultTargetPlatformOverride = TargetPlatform.android;
      answer((call) => {
            'approval': {...part('tee'), 'appGated': true},
            'presence': part('tee'),
          });
      expect((await store.generateKeys()).approval.gestureRequired, isFalse, reason: 'a phone cannot report the app gate: its hardware binds the gesture or it is refused');
    });

    test('status on a phone (a plugin without probe) is ready, and on a platform without a key store it throws', () async {
      answer((call) {
        if (call.method == 'probe') throw MissingPluginException('no probe');
        return 'missing';
      });
      expect(await store.status(), KeyStoreStatus.ready);
      answer((call) => throw MissingPluginException('no plugin'));
      await expectLater(store.status(), throwsA(isA<MissingPluginException>()));
    });

    test('keysState maps the plugin answer and fails closed', () async {
      for (final (wire, state) in [
        ('ok', KeysState.ok),
        ('approval_key_invalidated', KeysState.approvalKeyInvalidated),
        ('missing', KeysState.missing),
        ('anything else', KeysState.missing),
        (null, KeysState.missing),
      ]) {
        answer((call) => wire);
        expect(await store.keysState(), state, reason: '$wire');
      }
    });

    test('signPresence never carries prompt text; signApproval does', () async {
      final calls = <MethodCall>[];
      answer((call) {
        calls.add(call);
        return Uint8List(64);
      });
      await store.signPresence(Uint8List(3));
      await store.signApproval(Uint8List(3),
          promptTitle: 'Approve this action', promptSubtitle: 'Bank: 10 EUR');
      expect(calls[0].method, 'signPresence');
      expect((calls[0].arguments as Map).keys, ['data']);
      expect(calls[1].method, 'signApproval');
      expect((calls[1].arguments as Map)['title'], 'Approve this action');
      expect((calls[1].arguments as Map)['subtitle'], 'Bank: 10 EUR');
    });

    test('prompt failures become typed exceptions', () async {
      Future<Object?> run(String code) async {
        messenger.setMockMethodCallHandler(
            channel, (call) async => throw PlatformException(code: code, message: 'm'));
        try {
          await store.signApproval(Uint8List(3), promptTitle: 't', promptSubtitle: 's');
        } on Object catch (e) {
          return e;
        }
        return null;
      }

      expect(await run('cancelled'), isA<KeyPromptCancelled>());
      expect(await run('approval_key_invalidated'), isA<ApprovalKeyInvalidated>());
      for (final code in ['no_biometrics', 'locked_out', 'no_activity', 'auth_failed']) {
        final e = await run(code);
        expect(e, isA<KeyPromptFailed>(), reason: code);
        expect((e as KeyPromptFailed).code, code);
      }
      expect(await run('keystore_error'), isA<PlatformException>());
      // The Windows key store: a vanished key is a missing one (enroll again), a refusal of the TPM is a plain failure, and no internal status reaches the user.
      expect(await run('key_missing'), isA<ApprovalKeysMissing>());
      for (final code in ['sign_failed', 'key_failed']) {
        final e = await run(code);
        expect(e, isA<KeyPromptFailed>(), reason: code);
        expect((e as KeyPromptFailed).message, 'The device key could not sign. Try again.');
      }
    });

    test('signPresence says a Windows key store failure without its internal status', () async {
      messenger.setMockMethodCallHandler(
          channel, (call) async => throw PlatformException(code: 'key_missing', message: 'x'));
      await expectLater(store.signPresence(Uint8List(3)), throwsA(isA<ApprovalKeysMissing>()), reason: 'a key that is gone needs a new enrollment, not a retry');
      for (final code in ['sign_failed', 'key_failed']) {
        messenger.setMockMethodCallHandler(
            channel, (call) async => throw PlatformException(code: code, message: 'The TPM did not sign (status -2146893792).'));
        await expectLater(
            store.signPresence(Uint8List(3)),
            throwsA(isA<KeyPromptFailed>().having((e) => e.message, 'message', 'The device key could not sign. Try again.')),
            reason: code);
      }
      messenger.setMockMethodCallHandler(channel, (call) async => throw PlatformException(code: 'keystore_error', message: 'x'));
      await expectLater(store.signPresence(Uint8List(3)), throwsA(isA<PlatformException>()));
    });

    test('the kid is derived from the key (RFC 7638 thumbprint) and stable', () {
      final a = DeviceKey.fromParts(KeyUse.approve,
          x: 'f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU',
          y: 'x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0',
          securityLevel: KeySecurityLevel.strongBox);
      expect(a.thumbprint, hasLength(43));
      expect(a.kid, 'biatec-approve-${a.thumbprint.substring(0, 32)}');
    });

    test('a refusal from the plugin reaches the caller', () async {
      messenger.setMockMethodCallHandler(channel, (call) async {
        throw PlatformException(
            code: 'insecure_key',
            message: 'The keystore holds the key in software');
      });
      expect(store.generateKeys(), throwsA(isA<PlatformException>()));
    });
  });
}
