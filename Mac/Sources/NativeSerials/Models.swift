import Foundation

enum ProductSerialFilter: Int, CaseIterable {
    case all, withSerials, withoutSerials
    func includes(_ serial: String) -> Bool {
        switch self {
        case .all: return true
        case .withSerials: return !serial.isEmpty
        case .withoutSerials: return serial.isEmpty
        }
    }
}

struct NativeProduct: Hashable, Sendable {
    var key: String
    var name: String
    var upid: String
    var snpid: String
    var company: String
    var productType: String
    var contentVersion: String
    var contentDir: String
    var installDir: String
    var serial: String
    var hasRas3Token: Bool
    var isOwnedCandidate: Bool
    var sources: [String]
    var registryName: String = ""
}

struct ScanSummary {
    var productCount: Int
    var installedJsonCount: Int
    var serviceXmlCount: Int
    var ras3TokenCount: Int
    var decodedTokenCount: Int
    var serialCount: Int
    var sourceRoots: [String]
}

struct ScanResult {
    var products: [NativeProduct]
    var summary: ScanSummary
}
