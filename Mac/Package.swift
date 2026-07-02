// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "NativeSerials",
    platforms: [
        .macOS(.v13)
    ],
    products: [
        .executable(name: "NativeSerials", targets: ["NativeSerials"])
    ],
    targets: [
        .executableTarget(
            name: "NativeSerials",
            linkerSettings: [
                .linkedFramework("AppKit")
            ]
        )
    ]
)

