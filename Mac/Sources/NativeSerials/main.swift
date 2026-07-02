import AppKit
import Foundation

if CommandLine.arguments.contains("--scan-summary") {
    let result = NativeScanner().scan()
    let summary = result.summary
    print("Products: \(summary.productCount)")
    print("Installed product JSON files: \(summary.installedJsonCount)")
    print("Service Center XML files: \(summary.serviceXmlCount)")
    print("RAS3 token files: \(summary.ras3TokenCount)")
    print("Decoded RAS3 tokens: \(summary.decodedTokenCount)")
    print("Products with serials: \(summary.serialCount)")
    exit(0)
}

let app = NSApplication.shared
let delegate = AppController()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
