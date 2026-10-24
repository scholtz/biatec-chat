import 'package:biatec_authenticator/config.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
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
}
