import SwiftUI
import MaskingCore

/// 右インスペクタ（wp5 §2）。種別ピッカー / warnings / 候補リスト / 手動リスト。
struct CandidateListView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.undoManager) private var undoManager
    let page: PageState

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                typeSection
                warningsSection
                candidateSection
                manualSection
            }
            .padding(12)
        }
        .frame(width: 320)
    }

    // MARK: - 種別

    private var typeBinding: Binding<DocumentType> {
        Binding(
            get: { page.analyzed.preset.documentType },
            set: { appState.requestTypeChange(page: page, to: $0) }
        )
    }

    private var typeSection: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("種別").font(.headline)
            Picker("種別", selection: typeBinding) {
                ForEach(page.analyzed.classification.ranking, id: \.type) { entry in
                    Text("\(appState.analysis.displayName(for: entry.type))（\(entry.score)）")
                        .tag(entry.type)
                }
            }
            .labelsHidden()
            .accessibilityIdentifier("review.typePicker")

            Button {
                appState.requestCropAdjust(page: page)
            } label: {
                Label("切り抜きを調整…", systemImage: "crop")
            }
            .accessibilityIdentifier("review.cropButton")
            .help("自動の切り抜きがおかしいとき、四隅を手動で指定し直します")
        }
    }

    // MARK: - warnings

    @ViewBuilder
    private var warningsSection: some View {
        if !page.analyzed.preset.warnings.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                ForEach(page.analyzed.preset.warnings, id: \.self) { w in
                    HStack(alignment: .top, spacing: 6) {
                        Image(systemName: "exclamationmark.triangle.fill")
                            .foregroundStyle(.orange)
                        Text(w).font(.callout)
                    }
                }
            }
            .padding(8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color.yellow.opacity(0.2))
            .clipShape(RoundedRectangle(cornerRadius: 6))
            .accessibilityIdentifier("review.warnings")
        }
    }

    // MARK: - 候補リスト

    private var candidateSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("マスク候補").font(.headline)
            if page.analyzed.candidates.isEmpty {
                Text("マスク候補が見つかりませんでした。手動ツールで塗ってください。")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .accessibilityIdentifier("review.candidates.empty")
            } else {
                ForEach(page.analyzed.candidates) { cand in
                    candidateRow(cand)
                }
            }
        }
    }

    private func candidateRow(_ cand: MaskCandidate) -> some View {
        let isOnBinding = Binding(get: { cand.isOn }, set: { _ in page.toggleCandidate(cand.id) })
        return VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 6) {
                Toggle("", isOn: isOnBinding)
                    .labelsHidden()
                    .accessibilityIdentifier("review.candidate.\(cand.ruleID).toggle")
                Text(cand.label).font(.body)
                Spacer()
                Text(sourceBadge(cand.source))
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                if let conf = cand.confidence {
                    Text("\(Int(conf * 100))%")
                        .font(.caption).foregroundStyle(.secondary).monospacedDigit()
                }
            }
            // basis は全候補で常時表示（絶対条件・wp5 §2-3）。
            Text(cand.basis)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(6)
        .background(page.selectedCandidateID == cand.id ? Color.accentColor.opacity(0.12) : Color.clear)
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .contentShape(Rectangle())
        .accessibilityIdentifier("review.candidate.\(cand.ruleID)")
        .onHover { hovering in
            if hovering { page.selectedCandidateID = cand.id }
            else if page.selectedCandidateID == cand.id { page.selectedCandidateID = nil }
        }
    }

    private func sourceBadge(_ source: MaskCandidate.Source) -> String {
        switch source {
        case .fixedRegion: return "〈固定〉"
        case .detector: return "〈自動〉"
        }
    }

    // MARK: - 手動リスト

    @ViewBuilder
    private var manualSection: some View {
        let items = page.manualItems
        if !items.isEmpty {
            VStack(alignment: .leading, spacing: 8) {
                Text("手動マスク").font(.headline)
                ForEach(Array(items.enumerated()), id: \.offset) { index, item in
                    HStack {
                        Text(manualLabel(item))
                            .font(.body)
                            .accessibilityIdentifier("review.manual.row.\(index)")
                        Spacer()
                        Button(role: .destructive) {
                            page.deleteManualItem(at: index, undo: undoManager)
                        } label: {
                            Image(systemName: "trash")
                        }
                        .buttonStyle(.borderless)
                        .accessibilityIdentifier("review.manual.delete.\(index)")
                    }
                    .padding(6)
                }
            }
        }
    }

    private func manualLabel(_ item: PageState.ManualItem) -> String {
        switch item {
        case .rect(let i): return "矩形 \(i + 1)"
        case .stroke(let i): return "ブラシ \(i + 1)"
        }
    }
}
