import 'package:flutter/material.dart';

class AppRoute {
  const AppRoute({
    required this.path,
    required this.title,
    required this.description,
    required this.icon,
  });

  final String path;
  final String title;
  final String description;
  final IconData icon;
}

/// All screens named in the product spec, reachable from the home list. The
/// screens themselves are wired to their services in `main.dart`.
const List<AppRoute> appRoutes = [
  AppRoute(
    path: '/enrollment',
    title: 'Enrollment',
    description: 'Enroll this device.',
    icon: Icons.phonelink_setup,
  ),
  AppRoute(
    path: '/pending',
    title: 'Pending approvals',
    description: 'No pending approvals.',
    icon: Icons.pending_actions,
  ),
  AppRoute(
    path: '/history',
    title: 'Approval history',
    description: 'No approvals yet.',
    icon: Icons.history,
  ),
  AppRoute(
    path: '/devices',
    title: 'Device management',
    description: 'Enrolled devices appear here.',
    icon: Icons.devices,
  ),
  AppRoute(
    path: '/clients',
    title: 'Trusted clients and thresholds',
    description: 'Trusted clients and per-client thresholds appear here.',
    icon: Icons.verified_user,
  ),
  AppRoute(
    path: '/security',
    title: 'Security settings',
    description: 'Biometric requirement, threshold and blocked clients.',
    icon: Icons.security,
  ),
];
