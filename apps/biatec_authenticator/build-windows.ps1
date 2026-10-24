# Local Windows build against the stage Biatec OIDC (config/local.json). Extra arguments go to flutter.
flutter build windows --dart-define-from-file=config/local.json @args
