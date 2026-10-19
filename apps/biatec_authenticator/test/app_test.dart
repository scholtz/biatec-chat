import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/main.dart';
import 'package:biatec_authenticator/routes.dart';
import 'package:flutter/widgets.dart' show Size;
import 'package:flutter_test/flutter_test.dart';

void main() {
  void tall(WidgetTester tester) {
    tester.view.physicalSize = const Size(800, 2400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  test('config defaults to the mock OIDC', () {
    final config = AppConfig.resolve('');
    expect(config.isMock, isTrue);
    expect(config.oidcBaseUrl, AppConfig.mockOidcBaseUrl);
  });

  test('config honors an explicit base URL', () {
    final config = AppConfig.resolve('https://oidc.example');
    expect(config.isMock, isFalse);
    expect(config.oidcBaseUrl, 'https://oidc.example');
  });

  test('config strips trailing slashes', () {
    final config = AppConfig.resolve('http://10.0.2.2:5080/mock-oidc/');
    expect(config.isMock, isTrue);
    expect(config.oidcBaseUrl, AppConfig.mockOidcBaseUrl);
  });

  testWidgets('home shows mock OIDC config', (tester) async {
    tall(tester);
    await tester.pumpWidget(AuthenticatorApp(config: AppConfig.resolve('')));
    expect(find.text('Biatec Authenticator'), findsOneWidget);
    expect(find.text(AppConfig.mockOidcBaseUrl), findsOneWidget);
    expect(find.text('Biatec OIDC (mock)'), findsOneWidget);
  });

  testWidgets('every screen is reachable from home', (tester) async {
    tall(tester);
    await tester.pumpWidget(AuthenticatorApp(config: AppConfig.resolve('')));
    for (final route in appRoutes) {
      // Enrollment is a real screen (covered in test/enrollment) that needs
      // platform plugins, so it is not opened here.
      if (route.path == '/enrollment') continue;
      await tester.tap(find.text(route.title));
      await tester.pumpAndSettle();
      expect(find.text(route.description), findsOneWidget,
          reason: route.title);
      await tester.pageBack();
      await tester.pumpAndSettle();
    }
  });
}
