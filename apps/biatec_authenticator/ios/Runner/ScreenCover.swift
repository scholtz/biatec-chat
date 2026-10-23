import UIKit

/// Hides the app's content from the app switcher snapshot and while the screen is recorded or mirrored (BCHAT-031): a cover view is put over
/// every window of the app while the app is not active or the screen is captured, and removed again afterwards. Windows that appear while
/// the cover is up (a new scene, an alert, the keyboard) are covered as they become visible.
final class ScreenCover {
    static let shared = ScreenCover()
    private var covers: [ObjectIdentifier: UIView] = [:]
    private var inactive = false
    private var started = false

    func start() {
        guard !started else { return }
        started = true
        let center = NotificationCenter.default
        // applicationState is still .active inside willResignActive, so the state is tracked from the notifications themselves.
        center.addObserver(forName: UIApplication.willResignActiveNotification, object: nil, queue: .main) { [weak self] _ in
            self?.inactive = true
            self?.update()
        }
        center.addObserver(forName: UIApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            self?.inactive = false
            self?.update()
        }
        center.addObserver(forName: UIScreen.capturedDidChangeNotification, object: nil, queue: .main) { [weak self] _ in self?.update() }
        center.addObserver(forName: UIWindow.didBecomeVisibleNotification, object: nil, queue: .main) { [weak self] _ in self?.update() }
    }

    private func update() {
        if inactive || isCaptured() { show() } else { hide() }
    }

    private func isCaptured() -> Bool {
        UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .contains { $0.screen.isCaptured }
    }

    private func show() {
        for scene in UIApplication.shared.connectedScenes.compactMap({ $0 as? UIWindowScene }) {
            for window in scene.windows where covers[ObjectIdentifier(window)] == nil {
                let cover = UIView(frame: window.bounds)
                cover.autoresizingMask = [.flexibleWidth, .flexibleHeight]
                cover.backgroundColor = .systemBackground
                window.addSubview(cover)
                covers[ObjectIdentifier(window)] = cover
            }
        }
    }

    private func hide() {
        covers.values.forEach { $0.removeFromSuperview() }
        covers.removeAll()
    }
}
