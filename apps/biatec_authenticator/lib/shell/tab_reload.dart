import 'package:flutter/widgets.dart';

/// Tells the first screen of a tab that it was shown again after a while away, so it loads its data again in place. The screen is not rebuilt: what it shows, what the user typed and an action that is running all stay.
class TabReload extends InheritedWidget {
  const TabReload({super.key, required this.reload, required super.child});

  /// Notifies once per reload.
  final Listenable reload;

  /// The reload signal of the tab around [context], or null outside the shell.
  static Listenable? of(BuildContext context) =>
      context.dependOnInheritedWidgetOfExactType<TabReload>()?.reload;

  @override
  bool updateShouldNotify(TabReload oldWidget) => reload != oldWidget.reload;
}

/// Calls [onReload] each time the tab around the state is reloaded (see [TabReload]).
mixin TabReloadListener<T extends StatefulWidget> on State<T> {
  Listenable? _reload;

  void onReload();

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final reload = TabReload.of(context);
    if (identical(reload, _reload)) return;
    _reload?.removeListener(onReload);
    _reload = reload?..addListener(onReload);
  }

  @override
  void dispose() {
    _reload?.removeListener(onReload);
    super.dispose();
  }
}
