import Foundation

struct NativeProduct: Hashable {
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
