import SwiftUI

@main
struct LocalBridgeApp: App {
    @StateObject private var receiver = ReceiverViewModel()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(receiver)
        }
    }
}

