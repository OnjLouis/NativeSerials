import Foundation

final class NativeScanner {
    private let fileManager = FileManager.default

    private var sharedRoot: URL {
        URL(fileURLWithPath: "/Users/Shared/Native Instruments", isDirectory: true)
    }

    private var installedProductsURL: URL {
        sharedRoot.appendingPathComponent("installed_products", isDirectory: true)
    }

    private var ras3URL: URL {
        sharedRoot.appendingPathComponent("Native Access", isDirectory: true)
            .appendingPathComponent("ras3", isDirectory: true)
    }

    private var serviceCenterURL: URL {
        URL(fileURLWithPath: "/Library/Application Support/Native Instruments/Service Center", isDirectory: true)
    }

    func scan() -> ScanResult {
        var productsByKey: [String: NativeProduct] = [:]
        let installed = scanInstalledProducts()
        let service = scanServiceCenter()
        let tokens = scanRas3Tokens()

        for product in installed {
            productsByKey[product.key] = product
        }

        for product in service {
            let key = product.upid.isEmpty ? "service:\(product.name)" : "upid:\(product.upid)"
            if var existing = productsByKey[key] {
                merge(&existing, with: product)
                productsByKey[key] = existing
            } else {
                productsByKey[key] = product
            }
        }

        for token in tokens.products {
            let key = token.upid.isEmpty ? "token:\(token.key)" : "upid:\(token.upid)"
            if var existing = productsByKey[key] {
                merge(&existing, with: token)
                productsByKey[key] = existing
            } else {
                productsByKey[key] = token
            }
        }

        let products = dedupeByName(Array(productsByKey.values).filter { $0.isOwnedCandidate }).sorted {
            $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }
        let summary = ScanSummary(
            productCount: products.count,
            installedJsonCount: installed.count,
            serviceXmlCount: service.count,
            ras3TokenCount: tokens.fileCount,
            decodedTokenCount: tokens.decodedCount,
            serialCount: products.filter { !$0.serial.isEmpty }.count,
            sourceRoots: [
                installedProductsURL.path,
                ras3URL.path,
                serviceCenterURL.path,
                sharedRoot.appendingPathComponent("Logs/NTK").path
            ]
        )
        return ScanResult(products: products, summary: summary)
    }

    private func scanInstalledProducts() -> [NativeProduct] {
        guard let files = try? fileManager.contentsOfDirectory(
            at: installedProductsURL,
            includingPropertiesForKeys: nil
        ) else { return [] }

        return files.filter { $0.pathExtension.lowercased() == "json" }.compactMap { url in
            guard let data = try? Data(contentsOf: url),
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                return nil
            }
            let name = url.deletingPathExtension().lastPathComponent
            return NativeProduct(
                key: "installed:\(name)",
                name: name,
                upid: "",
                snpid: "",
                company: "",
                productType: "",
                contentVersion: string(json["ContentVersion"]),
                contentDir: string(json["ContentDir"]),
                installDir: string(json["InstallDir"]),
                serial: "",
                hasRas3Token: false,
                isOwnedCandidate: true,
                sources: [url.path]
            )
        }
    }

    private func scanServiceCenter() -> [NativeProduct] {
        guard let files = try? fileManager.contentsOfDirectory(
            at: serviceCenterURL,
            includingPropertiesForKeys: nil
        ) else { return [] }

        return files.filter { $0.pathExtension.lowercased() == "xml" }.flatMap { url -> [NativeProduct] in
            guard let text = try? String(contentsOf: url, encoding: .utf8) else { return [] }
            let blocks = productBlocks(in: text)
            guard blocks.isEmpty else {
                return blocks.map { block in
                    serviceProduct(from: block, source: url)
                }
            }
            let upid = firstTag("UPID", in: text)
            let xmlName = firstTag("Name", in: text)
            let name = xmlName.isEmpty ? url.deletingPathExtension().lastPathComponent : xmlName
            let serial = firstTag("Serial", in: text)
            return [NativeProduct(
                key: upid.isEmpty ? "service:\(name)" : "upid:\(upid)",
                name: name,
                upid: upid,
                snpid: firstTag("SNPID", in: text),
                company: firstTag("Company", in: text),
                productType: firstTag("Type", in: text),
                contentVersion: "",
                contentDir: "",
                installDir: "",
                serial: serial,
                hasRas3Token: false,
                isOwnedCandidate: false,
                sources: [url.path]
            )]
        }
    }

    private func serviceProduct(from text: String, source url: URL) -> NativeProduct {
        let upid = firstTag("UPID", in: text)
        let xmlName = firstTag("Name", in: text)
        let name = xmlName.isEmpty ? url.deletingPathExtension().lastPathComponent : xmlName
        return NativeProduct(
            key: upid.isEmpty ? "service:\(name)" : "upid:\(upid)",
            name: name,
            upid: upid,
            snpid: firstTag("SNPID", in: text),
            company: firstTag("Company", in: text),
            productType: firstTag("Type", in: text),
            contentVersion: "",
            contentDir: "",
            installDir: "",
            serial: firstTag("Serial", in: text),
            hasRas3Token: false,
            isOwnedCandidate: false,
            sources: [url.path]
        )
    }

    private func scanRas3Tokens() -> (products: [NativeProduct], fileCount: Int, decodedCount: Int) {
        guard let files = try? fileManager.contentsOfDirectory(
            at: ras3URL,
            includingPropertiesForKeys: nil
        ) else { return ([], 0, 0) }

        var decodedCount = 0
        let jwtFiles = files.filter { $0.pathExtension.lowercased() == "jwt" }
        let products = jwtFiles.compactMap { url -> NativeProduct? in
            guard let token = try? String(contentsOf: url, encoding: .utf8),
                  let payload = decodeJwtPayload(token) else {
                return nil
            }
            decodedCount += 1
            let upid = url.deletingPathExtension().lastPathComponent
            let serial = string(payload["serial"])
            return NativeProduct(
                key: "upid:\(upid)",
                name: "License token \(upid.prefix(8))",
                upid: upid,
                snpid: "",
                company: "",
                productType: "",
                contentVersion: "",
                contentDir: "",
                installDir: "",
                serial: serial,
                hasRas3Token: true,
                isOwnedCandidate: true,
                sources: [url.path]
            )
        }
        return (products, jwtFiles.count, decodedCount)
    }

    private func merge(_ existing: inout NativeProduct, with other: NativeProduct) {
        if existing.name.hasPrefix("License token "), !other.name.isEmpty { existing.name = other.name }
        if existing.upid.isEmpty { existing.upid = other.upid }
        if existing.snpid.isEmpty { existing.snpid = other.snpid }
        if existing.company.isEmpty { existing.company = other.company }
        if existing.productType.isEmpty { existing.productType = other.productType }
        if existing.contentVersion.isEmpty { existing.contentVersion = other.contentVersion }
        if existing.contentDir.isEmpty { existing.contentDir = other.contentDir }
        if existing.installDir.isEmpty { existing.installDir = other.installDir }
        if existing.serial.isEmpty { existing.serial = other.serial }
        existing.hasRas3Token = existing.hasRas3Token || other.hasRas3Token
        existing.isOwnedCandidate = existing.isOwnedCandidate || other.isOwnedCandidate
        existing.sources.append(contentsOf: other.sources.filter { !existing.sources.contains($0) })
    }

    private func dedupeByName(_ products: [NativeProduct]) -> [NativeProduct] {
        var grouped: [String: NativeProduct] = [:]
        var order: [String] = []
        for product in products {
            let key = normalizedName(product.name)
            guard !key.isEmpty else { continue }
            if var existing = grouped[key] {
                merge(&existing, with: product)
                grouped[key] = existing
            } else {
                grouped[key] = product
                order.append(key)
            }
        }
        return order.compactMap { grouped[$0] }
    }

    private func normalizedName(_ value: String) -> String {
        value.trimmingCharacters(in: .whitespacesAndNewlines)
            .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: .current)
            .replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
    }

    private func decodeJwtPayload(_ token: String) -> [String: Any]? {
        let parts = token.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: ".")
        guard parts.count >= 2 else { return nil }
        var base64 = String(parts[1]).replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        let pad = base64.count % 4
        if pad > 0 { base64 += String(repeating: "=", count: 4 - pad) }
        guard let data = Data(base64Encoded: base64),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return nil
        }
        return json
    }

    private func firstTag(_ tag: String, in text: String) -> String {
        guard let open = text.range(of: "<\(tag)>"),
              let close = text.range(of: "</\(tag)>", range: open.upperBound..<text.endIndex) else {
            return ""
        }
        let raw = String(text[open.upperBound..<close.lowerBound])
        return raw.replacingOccurrences(of: "&amp;", with: "&")
            .replacingOccurrences(of: "&lt;", with: "<")
            .replacingOccurrences(of: "&gt;", with: ">")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func productBlocks(in text: String) -> [String] {
        var blocks: [String] = []
        var searchStart = text.startIndex
        while let open = text.range(of: "<Product", range: searchStart..<text.endIndex),
              let startClose = text.range(of: ">", range: open.upperBound..<text.endIndex),
              let close = text.range(of: "</Product>", range: startClose.upperBound..<text.endIndex) {
            blocks.append(String(text[startClose.upperBound..<close.lowerBound]))
            searchStart = close.upperBound
        }
        return blocks
    }

    private func string(_ value: Any?) -> String {
        if let value = value as? String { return value }
        if let value = value as? CustomStringConvertible { return value.description }
        return ""
    }
}
