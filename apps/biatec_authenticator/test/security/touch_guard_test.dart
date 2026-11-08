// T-220: Android 10 and 11 let a partly covered touch reach the window. The flag of each touch is noted by the activity and Dart asks for it on the approve action alone.
import 'dart:io';

import 'package:biatec_authenticator/security/touch_guard.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  final messenger =
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;

  void handle(Future<Object?>? Function(MethodCall) answer) {
    messenger.setMockMethodCallHandler(PlatformTouchGuard.channel, answer);
    addTearDown(() =>
        messenger.setMockMethodCallHandler(PlatformTouchGuard.channel, null));
  }

  group('the Dart side', () {
    test('passes on what the activity noted', () async {
      handle((c) async => c.method == 'partiallyObscured' ? true : null);
      expect(await const PlatformTouchGuard().partiallyObscured(), isTrue);
      handle((c) async => false);
      expect(await const PlatformTouchGuard().partiallyObscured(), isFalse);
    });

    test('a platform without the channel has nothing to check', () async {
      expect(await const PlatformTouchGuard().partiallyObscured(), isFalse);
    });

    test('a failing check blocks the approval (the stricter reading)',
        () async {
      handle((c) async => throw PlatformException(code: 'boom'));
      expect(await const PlatformTouchGuard().partiallyObscured(), isTrue);
    });

    test('the default guard obscures nothing', () async {
      expect(await const NoTouchGuard().partiallyObscured(), isFalse);
    });
  });

  group('the Android side', () {
    final kotlin = File(
            'android/app/src/main/kotlin/io/biatec/authenticator/MainActivity.kt')
        .readAsStringSync();

    test('notes the flag of every touch on Android 10 and 11 only', () {
      expect(kotlin, contains('override fun dispatchTouchEvent'));
      expect(kotlin, contains('FLAG_WINDOW_IS_PARTIALLY_OBSCURED'));
      expect(kotlin, contains('Build.VERSION.SDK_INT < 31'));
      expect(kotlin, contains('ACTION_DOWN'),
          reason: 'a new gesture starts from a clean flag');
    });

    test('answers on the channel the Dart side uses, and clears when read', () {
      expect(kotlin, contains('io.biatec.authenticator/touch'));
      expect(PlatformTouchGuard.channel.name, 'io.biatec.authenticator/touch');
      expect(kotlin, contains('"partiallyObscured"'));
      expect(kotlin, contains('obscuredGesture = false'));
    });

    test('does not drop every partly covered touch window-wide', () {
      // That would lock the app whenever a chat head or screen filter is on screen.
      expect(kotlin, isNot(contains('filterTouchesWhenObscured = false')));
      expect(kotlin, isNot(contains('return false')),
          reason: 'dispatchTouchEvent never swallows a touch itself');
    });
  });
}
