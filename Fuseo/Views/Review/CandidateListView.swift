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
                textStyleSection
                typeSection
                warningsSection
                candidateSection
                manualSection
                textListSection
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
            // WP-13: 選択肢は classification.ranking ではなく**プリセット全件**から作る
            // （ライブラリから復元したページは ranking が空のため）。スコアは分かるものだけ添える。
            Picker("種別", selection: typeBinding) {
                ForEach(appState.typeOptions(for: page)) { option in
                    Text(appState.typeOptionLabel(option)).tag(option.type)
                }
            }
            .labelsHidden()
            .disabled(appState.reanalyzing)
            .accessibilityIdentifier("review.typePicker")

            Button {
                appState.requestCropAdjust(page: page)
            } label: {
                Label("切り抜きを調整…", systemImage: "crop")
            }
            .disabled(appState.reanalyzing)
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

    // MARK: - 文字パネル（文字入れ 0.3.0・docs/form-fill-design.md §5）

    /// 文字ツールのとき、または文字枠を選んでいるときに出す。選んでいればその枠、いなければ次に作る枠の設定を変える。
    @ViewBuilder
    private var textStyleSection: some View {
        if appState.tool == .text || page.selectedText != nil {
            let selected = page.selectedText
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("文字").font(.headline)
                    Spacer()
                    Text(selected == nil ? "次に作る枠の設定" : "選択中の枠")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .accessibilityIdentifier("review.textPanel.target")
                }

                Picker("書体", selection: styleBinding(\.font)) {
                    Text("ゴシック").tag(TextBox.Font.gothic)
                    Text("明朝").tag(TextBox.Font.mincho)
                }
                .pickerStyle(.segmented)
                .accessibilityIdentifier("review.textPanel.font")

                numberRow("サイズ", value: styleBinding(\.fontSizePt), range: TextBox.fontSizeRange,
                          step: TextBox.fontSizeStep, unit: "pt", id: "review.textPanel.size")
                numberRow("行間", value: styleBinding(\.lineSpacing), range: TextBox.lineSpacingRange,
                          step: TextBox.lineSpacingStep, unit: "倍", id: "review.textPanel.lineSpacing")
                numberRow("字間", value: styleBinding(\.letterSpacing), range: TextBox.letterSpacingRange,
                          step: TextBox.letterSpacingStep, unit: "字", id: "review.textPanel.letterSpacing")
                    .disabled(currentStyle.cells > 0)
                    .help(currentStyle.cells > 0
                          ? "マス目の欄では字間を使いません（枠の幅とマス数で位置が決まります）"
                          : "文字と文字の間に足す量（文字サイズの何倍か）。0 が標準、0.5 で半文字分あきます")

                Picker("横", selection: styleBinding(\.hAlign)) {
                    Text("左").tag(TextBox.HAlign.left)
                    Text("中央").tag(TextBox.HAlign.center)
                    Text("右").tag(TextBox.HAlign.right)
                }
                .pickerStyle(.segmented)
                .accessibilityIdentifier("review.textPanel.hAlign")

                Picker("縦", selection: styleBinding(\.vAlign)) {
                    Text("上").tag(TextBox.VAlign.top)
                    Text("中央").tag(TextBox.VAlign.center)
                    Text("下").tag(TextBox.VAlign.bottom)
                }
                .pickerStyle(.segmented)
                .accessibilityIdentifier("review.textPanel.vAlign")

                HStack(spacing: 6) {
                    Text("マス数").frame(width: 44, alignment: .leading)
                    TextField("マス数", value: styleBinding(\.cells), format: .number)
                        .labelsHidden()
                        .frame(width: 56)
                        .multilineTextAlignment(.trailing)
                        .accessibilityIdentifier("review.textPanel.cells")
                    Stepper("マス数", value: styleBinding(\.cells), in: 0...TextBox.maxCells)
                        .labelsHidden()
                    Text("0 = マス目なし").font(.caption).foregroundStyle(.secondary)
                }
                .help("枠の幅を N 等分して 1 マスに 1 文字ずつ置きます（個人番号・郵便番号など）。マスの位置は枠の幅と左右の端で合わせてください")

                colorRow

                if let selected {
                    Divider()
                    Text("位置と大きさ（mm・ページ左上から）").font(.caption).foregroundStyle(.secondary)
                    Grid(alignment: .leading, horizontalSpacing: 6, verticalSpacing: 6) {
                        GridRow {
                            mmField("X", \.x, selected.id, "review.textPanel.x")
                            mmField("Y", \.y, selected.id, "review.textPanel.y")
                        }
                        GridRow {
                            mmField("幅", \.width, selected.id, "review.textPanel.width")
                            mmField("高さ", \.height, selected.id, "review.textPanel.height")
                        }
                    }
                    Button("枠の高さに合わせる") {
                        appState.fitSelectedTextToFrame(page: page, undo: undoManager)
                    }
                    .help("行数と行間から、文字が枠の高さの 9 割になる文字サイズを 1 回だけ計算して入れます（自動では追従しません）")
                    .accessibilityIdentifier("review.textPanel.fit")
                }
            }
            .padding(8)
            .background(Color.accentColor.opacity(0.06))
            .clipShape(RoundedRectangle(cornerRadius: 6))
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("review.textPanel")
        }
    }

    private var currentStyle: TextBox.Style { page.selectedText?.style ?? appState.nextTextStyle }

    /// 色: パレット（黒・青・赤・緑・茶・灰）＋「その他」（Mac 標準の色選択）。
    private var colorRow: some View {
        let current = currentStyle.color
        return HStack(spacing: 6) {
            Text("色").frame(width: 44, alignment: .leading)
            ForEach(Array(TextBox.Color.palette.enumerated()), id: \.offset) { index, entry in
                Button {
                    styleBinding(\.color).wrappedValue = entry.color
                } label: {
                    Circle()
                        .fill(Color(.sRGB, red: entry.color.r, green: entry.color.g, blue: entry.color.b))
                        .frame(width: 18, height: 18)
                        .overlay(Circle().strokeBorder(current == entry.color ? Color.accentColor : Color.gray.opacity(0.4),
                                                       lineWidth: current == entry.color ? 2.5 : 1))
                        .padding(1)
                }
                .buttonStyle(.plain)
                .help(entry.name)
                .accessibilityLabel(Text(entry.name))
                .accessibilityAddTraits(current == entry.color ? .isSelected : [])
                .accessibilityIdentifier("review.textPanel.color.\(index)")
            }
            ColorPicker("その他", selection: Binding<CGColor>(
                get: { CGColor(srgbRed: current.r, green: current.g, blue: current.b, alpha: 1) },
                set: { cg in
                    guard let rgb = cg.converted(to: CGColorSpace(name: CGColorSpace.sRGB)!, intent: .defaultIntent, options: nil),
                          let c = rgb.components, c.count >= 3 else { return }
                    let picked = TextBox.Color(r: Double(c[0]), g: Double(c[1]), b: Double(c[2]))
                    if picked != current { styleBinding(\.color).wrappedValue = picked }
                }), supportsOpacity: false)
                .labelsHidden()
                .help("その他の色（Mac の色選択）")
                .accessibilityIdentifier("review.textPanel.color.custom")
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("review.textPanel.color")
    }

    private func styleBinding<V>(_ keyPath: WritableKeyPath<TextBox.Style, V>) -> Binding<V> {
        Binding(
            get: { (page.selectedText?.style ?? appState.nextTextStyle)[keyPath: keyPath] },
            set: { value in appState.applyTextStyle(page: page, undo: undoManager) { $0[keyPath: keyPath] = value } }
        )
    }

    private func numberRow(_ title: String, value: Binding<Double>, range: ClosedRange<Double>,
                           step: Double, unit: String, id: String) -> some View {
        HStack(spacing: 6) {
            Text(title).frame(width: 44, alignment: .leading)
            TextField(title, value: value, format: .number.precision(.fractionLength(0...2)))
                .labelsHidden()
                .frame(width: 64)
                .multilineTextAlignment(.trailing)
                .accessibilityIdentifier(id)
            Stepper(title, value: value, in: range, step: step)
                .labelsHidden()
                .accessibilityIdentifier("\(id).stepper")
            Text(unit).foregroundStyle(.secondary)
        }
    }

    private func mmField(_ title: String, _ keyPath: WritableKeyPath<TextGeometry.FrameMM, Double>,
                         _ id: TextBox.ID, _ axID: String) -> some View {
        let binding = Binding<Double>(
            get: { page.textFrameMM(id)?[keyPath: keyPath] ?? 0 },
            set: { value in
                page.commitTextEditing(undo: undoManager)
                guard var frame = page.textFrameMM(id) else { return }
                frame[keyPath: keyPath] = value
                page.setTextFrameMM(id, frame, undo: undoManager)
            })
        return HStack(spacing: 4) {
            Text(title).frame(width: 28, alignment: .leading)
            TextField(title, value: binding, format: .number.precision(.fractionLength(1)))
                .labelsHidden()
                .frame(width: 64)
                .multilineTextAlignment(.trailing)
                .accessibilityIdentifier(axID)
        }
    }

    // MARK: - 文字の一覧

    @ViewBuilder
    private var textListSection: some View {
        if !page.analyzed.texts.isEmpty {
            VStack(alignment: .leading, spacing: 8) {
                Text("文字").font(.headline)
                ForEach(Array(page.analyzed.texts.enumerated()), id: \.element.id) { index, t in
                    HStack {
                        Text(textRowLabel(t, index: index))
                            .font(.body)
                            .lineLimit(1)
                            .truncationMode(.tail)
                            .accessibilityIdentifier("review.text.row.\(index)")
                        Spacer()
                        Button(role: .destructive) {
                            page.selectedTextID = t.id
                            page.deleteSelectedText(undo: undoManager)
                        } label: {
                            Image(systemName: "trash")
                        }
                        .buttonStyle(.borderless)
                        .accessibilityIdentifier("review.text.delete.\(index)")
                    }
                    .padding(6)
                    .background(page.selectedTextID == t.id ? Color.accentColor.opacity(0.12) : Color.clear)
                    .clipShape(RoundedRectangle(cornerRadius: 6))
                    .contentShape(Rectangle())
                    .onTapGesture {
                        page.commitTextEditing(undo: undoManager)
                        page.selectedTextID = t.id
                    }
                }
            }
        }
    }

    private func textRowLabel(_ t: TextBox, index: Int) -> String {
        let lines = t.text.split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
        let firstLine = lines.first ?? ""
        if firstLine.isEmpty && lines.count <= 1 { return "文字 \(index + 1)（入力中）" }
        return lines.count > 1 ? "文字 \(index + 1): \(firstLine)（\(lines.count) 行）" : "文字 \(index + 1): \(firstLine)"
    }
}
