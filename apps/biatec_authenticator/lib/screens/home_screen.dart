import 'package:flutter/material.dart';

import '../config.dart';
import '../routes.dart';

class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key, required this.config});

  final AppConfig config;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Biatec Authenticator')),
      body: ListView(
        children: [
          for (final route in appRoutes)
            ListTile(
              leading: Icon(route.icon),
              title: Text(route.title),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => Navigator.of(context).pushNamed(route.path),
            ),
          const Divider(),
          ListTile(
            key: const Key('oidc-config'),
            leading: const Icon(Icons.dns),
            title: Text(config.isMock ? 'Biatec OIDC (mock)' : 'Biatec OIDC'),
            subtitle: Text(config.oidcBaseUrl),
          ),
        ],
      ),
    );
  }
}
