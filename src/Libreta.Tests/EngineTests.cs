using Shouldly;
using Xunit;

namespace Libreta.Tests;

public class EngineTests
{
  [Fact]
  public void TrialSubstitutionsPreserveCoordinateUnits()
  {
    // Arrange
    using var fixture = new BookFixture();
    View view = fixture.LoadView();
    var engine = new Engine(view, new Dictionary<(string Line, string Column), double>
    {
      [("analysis.override_link", "2023")] = 0.25,
      [("analysis.revenue", "2023")] = 25
    });

    // Act
    ResolvedCell cell = engine.Cell("override_link", "2023");
    ResolvedCell fact = engine.Cell("revenue", "2023");

    // Assert
    cell.Value.ShouldBe(0.25);
    cell.Units.ShouldBe("percent");
    cell.Kind.ShouldBe("trial");
    fact.Units.ShouldBe("millions");
    fact.Kind.ShouldBe("trial");
  }

  [Fact]
  public void TrialSubstitutionsReplaceFactsAndFormulasWithoutChangingTheBaseEngine()
  {
    // Arrange
    using var fixture = new BookFixture();
    View view = fixture.LoadView();
    var baseEngine = new Engine(view);
    var factTrial = new Engine(view, new Dictionary<(string Line, string Column), double>
    {
      [("analysis.revenue", "2023")] = 7
    });
    var formulaTrial = new Engine(view, new Dictionary<(string Line, string Column), double>
    {
      [("analysis.doubled", "2023")] = 11
    });

    // Act
    double? propagatedFact = factTrial.Value("doubled", "2023");
    ResolvedCell replacedFormula = formulaTrial.Cell("doubled", "2023");
    double? unchanged = baseEngine.Value("doubled", "2023");

    // Assert
    propagatedFact.ShouldBe(14);
    replacedFormula.Value.ShouldBe(11);
    replacedFormula.Kind.ShouldBe("trial");
    unchanged.ShouldBe(200);
  }

  [Fact]
  public void SensitivityTrialsAreIndependentAndRespectBookDefinedValidity()
  {
    // Arrange
    using var fixture = new BookFixture();
    File.WriteAllText(Path.Combine(fixture.Root, "sensitivities.yaml"), """
      sensitivities:
        value_grid:
          title: Value grid
          output:
            line: analysis.doubled
            column: 2023
          inputs:
            - line: analysis.revenue
              column: 2023
              label: Revenue
              values: [5, 10]
            - line: analysis.doubled
              column: 2025
              label: Limit
              values: [9, 21]
          valid: analysis.revenue["2023"] < analysis.doubled["2025"]
      """);
    File.AppendAllText(Path.Combine(fixture.ViewFolder, "view.yaml"), """

      sensitivities: [value_grid]
      """);
    View view = fixture.LoadView();

    // Act
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);
    SensitivityPayload sensitivity = payload.Sensitivities.Single();

    // Assert
    sensitivity.Cells[0][0].Exact.ShouldBe("10");
    sensitivity.Cells[0][1].Valid.ShouldBeFalse();
    sensitivity.Cells[1][1].Exact.ShouldBe("20");
    sensitivity.OutputLine.ShouldBe("analysis.doubled");
    sensitivity.OutputColumn.ShouldBe("2023");
    sensitivity.OutputSourceView.ShouldBe(view.Id);
    sensitivity.Inputs[0].SourceView.ShouldBe(view.Id);
    sensitivity.Inputs[1].SourceView.ShouldBe(view.Id);
    new Engine(view).Value("doubled", "2023").ShouldBe(200);
  }

  [Fact]
  public void OneInputSensitivityPropagatesAcrossViews()
  {
    // Arrange
    using var fixture = new BookFixture();
    File.WriteAllText(Path.Combine(fixture.Root, "sensitivities.yaml"), """
      sensitivities:
        linked:
          output:
            line: analysis.taxed
            column: 2023
          inputs:
            - line: model_assumptions.revenue_growth
              column: 2023
              values: [0.2, 0.3]
      """);
    File.AppendAllText(Path.Combine(fixture.PresentationFolder, "view.yaml"), """

      sensitivities: [linked]
      """);
    Book book = Book.Load(fixture.Root);
    View view = View.Load(book, fixture.PresentationFolder);

    // Act
    SensitivityPayload sensitivity = PayloadBuilder.Build(view, new Engine(view), 1).Sensitivities.Single();

    // Assert
    sensitivity.Cells.Count.ShouldBe(1);
    sensitivity.Cells[0].Select(cell => cell.Exact).ShouldBe(new[] { "20", "30" });
    sensitivity.Inputs.Single().Line.ShouldBe("model_assumptions.revenue_growth");
    sensitivity.Inputs.Single().SourceView.ShouldBe(view.Id);
  }

  [Fact]
  public void SensitivityRejectsUnknownColumnsEvenWhenTheFormulaIsConstant()
  {
    // Arrange
    using var fixture = new BookFixture();
    File.WriteAllText(Path.Combine(fixture.Root, "sensitivities.yaml"), """
      sensitivities:
        typo:
          output:
            line: model_assumptions.revenue_growth
            column: Typo
          inputs:
            - line: analysis.revenue
              column: 2023
              values: [1]
      """);
    File.AppendAllText(Path.Combine(fixture.ViewFolder, "view.yaml"), """

      sensitivities: [typo]
      """);
    View view = fixture.LoadView();

    // Act
    Action build = () => PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    build.ShouldThrow<InvalidDataException>().Message.ShouldContain("unknown output column 'Typo'");
  }

  [Fact]
  public void SensitivityKeepsOtherTrialsWhenOneTrialThrows()
  {
    // Arrange
    using var fixture = new BookFixture();
    File.WriteAllText(Path.Combine(fixture.Root, "sensitivities.yaml"), """
      sensitivities:
        guarded:
          output:
            line: analysis.doubled
            column: 2023
          inputs:
            - line: analysis.revenue
              column: 2023
              values: [5, 15]
          valid: if(analysis.revenue["2023"] < 10, 1, analysis.broken["2023"])
      """);
    File.AppendAllText(Path.Combine(fixture.ViewFolder, "view.yaml"), """

      sensitivities: [guarded]
      """);
    View view = fixture.LoadView();

    // Act
    SensitivityPayload sensitivity = PayloadBuilder.Build(view, new Engine(view), 1).Sensitivities.Single();

    // Assert
    sensitivity.Cells[0][0].Exact.ShouldBe("10");
    sensitivity.Cells[0][1].Valid.ShouldBeFalse();
    sensitivity.Cells[0][1].UnavailableReason!.ShouldContain("could not be calculated");
  }

  [Fact]
  public void SensitivityRejectsCanonicalDuplicateInputsAndKeepsZeroOutputs()
  {
    // Arrange
    using var fixture = new BookFixture();
    string sensitivityPath = Path.Combine(fixture.ViewFolder, "sensitivities.yaml");
    File.WriteAllText(sensitivityPath, """
      sensitivities:
        duplicate:
          output:
            line: analysis.doubled
            column: 2023
          inputs:
            - line: analysis.revenue
              column: 2023
              values: [0]
            - line: revenue
              column: 2023
              values: [0]
      """);
    File.AppendAllText(Path.Combine(fixture.ViewFolder, "view.yaml"), """

      sensitivities: [duplicate]
      """);
    Book duplicateBook = Book.Load(fixture.Root);
    View duplicateView = View.Load(duplicateBook, fixture.ViewFolder);

    // Act
    Action buildDuplicate = () => PayloadBuilder.Build(duplicateView, new Engine(duplicateView), 1);

    // Assert
    buildDuplicate.ShouldThrow<InvalidDataException>().Message.ShouldContain("repeats an input coordinate");

    // Arrange
    File.WriteAllText(sensitivityPath, """
      sensitivities:
        zero:
          output:
            line: analysis.doubled
            column: 2023
          inputs:
            - line: analysis.revenue
              column: 2023
              values: [0]
      """);
    File.WriteAllText(Path.Combine(fixture.ViewFolder, "view.yaml"), File.ReadAllText(
      Path.Combine(fixture.ViewFolder, "view.yaml")).Replace("sensitivities: [duplicate]", "sensitivities: [zero]"));
    Book zeroBook = Book.Load(fixture.Root);
    View zeroView = View.Load(zeroBook, fixture.ViewFolder);

    // Act
    SensitivityCellPayload zero = PayloadBuilder.Build(zeroView, new Engine(zeroView), 1)
      .Sensitivities.Single().Cells.Single().Single();

    // Assert
    zero.Valid.ShouldBeTrue();
    zero.Exact.ShouldBe("0");
    zero.UnavailableReason.ShouldBeNull();
  }

  [Fact]
  public void ResolvesFactsRowFormulasColumnFormulasAndCellOverrides()
  {
    // Arrange
    using var fixture = new BookFixture();
    View view = fixture.LoadView();
    var engine = new Engine(view);

    // Act
    ResolvedCell fact = engine.Cell("revenue", "2023");
    ResolvedCell rowFormula = engine.Cell("doubled", "2023");
    ResolvedCell columnFormula = engine.Cell("revenue", "cagr");
    ResolvedCell overridden = engine.Cell("doubled", "cagr");
    ResolvedCell formulaOverride = engine.Cell("spread", "cagr");
    ResolvedCell hardcodeOverride = engine.Cell("seeded", "2023");

    // Assert
    fact.Kind.ShouldBe("fact");
    fact.Value.ShouldBe(100);
    rowFormula.Kind.ShouldBe("formula");
    rowFormula.Value.ShouldBe(200);
    columnFormula.Kind.ShouldBe("formula");
    columnFormula.Value.ShouldNotBeNull();
    columnFormula.Value.Value.ShouldBe(0.1, 0.000000000001);
    columnFormula.Units.ShouldBe("percent");
    overridden.Kind.ShouldBe("blank");
    overridden.Value.ShouldBeNull();
    overridden.Units.ShouldBe("percent");
    formulaOverride.Kind.ShouldBe("formula");
    formulaOverride.Value.ShouldBe(21);
    formulaOverride.Units.ShouldBe("millions");
    hardcodeOverride.Kind.ShouldBe("fact");
    hardcodeOverride.Value.ShouldBe(1);
    hardcodeOverride.Formula.ShouldBeNull();
    hardcodeOverride.Expression.ShouldBeNull();
  }

  [Fact]
  public void ColumnFormulaCellKeepsItsLineUnits()
  {
    // Arrange
    using var fixture = new BookFixture();
    View view = fixture.LoadView();
    var engine = new Engine(view);

    // Act
    ResolvedCell undeclared = engine.Cell("linked_growth", "latest");
    ResolvedCell declared = engine.Cell("linked_growth", "cagr");
    ResolvedCell inherited = engine.Cell("doubled", "latest");

    // Assert
    undeclared.Units.ShouldBe("percent");
    declared.Units.ShouldBe("percent");
    inherited.Units.ShouldBe("millions");
  }

  [Fact]
  public void ResolvesExplicitCoordinatesWithoutRelativeTraversal()
  {
    // Arrange
    using var fixture = new BookFixture();
    View view = fixture.LoadView();
    var engine = new Engine(view);

    // Act
    ResolvedCell spread = engine.Cell("spread", "2023");

    // Assert
    spread.Value.ShouldBe(21);
  }

  [Fact]
  public void ResolvesNonFiniteExponentiationAsUnavailable()
  {
    // Arrange
    using var fixture = new BookFixture();
    View view = fixture.LoadView();
    var engine = new Engine(view);

    // Act
    ResolvedCell invalid = engine.Cell("invalid", "2023");

    // Assert
    invalid.Value.ShouldBeNull();
  }

  [Fact]
  public void PayloadPreservesCellSpecificProvenance()
  {
    // Arrange
    using var fixture = new BookFixture();
    View view = fixture.LoadView();

    // Act
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    payload.Columns.Select(column => column.Label).ShouldBe(new[] { "2023", "2025", "CAGR", "Pointer" });
    RowPayload revenue = payload.Rows.Single(row => row.Name.EndsWith(".revenue"));
    revenue.Cells[0].Kind.ShouldBe("fact");
    SourcePayload source = revenue.Cells[0].Source.ShouldNotBeNull();
    source.Image.ShouldBe("analysis/facts/source.png");
    source.Region.ShouldBe(new SourceRegionPayload(10, 20, 30, 40));
    source.Details.Keys.ShouldBe(new[] { "Page", "Document" });
    source.Details["Page"].ShouldBe("F-6");
    revenue.Cells[2].Kind.ShouldBe("formula");
    revenue.Cells[2].Units.ShouldBe("percent");
    revenue.Cells[2].Source.ShouldBeNull();
    revenue.Cells[2].Calculation.ShouldNotBeNull();
    RowPayload seeded = payload.Rows.Single(row => row.Name.EndsWith(".seeded"));
    seeded.Cells[0].Kind.ShouldBe("fact");
    seeded.Cells[0].Formula.ShouldBeNull();
    seeded.Cells[0].Calculation.ShouldBeNull();
  }

  [Fact]
  public void PayloadPreservesSourceAndModelingNotesAtTheirDefinitionScopes()
  {
    // Arrange
    using var fixture = new BookFixture();
    View view = fixture.LoadView();

    // Act
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    RowPayload revenue = payload.Rows.Single(row => row.Name.EndsWith(".revenue"));
    revenue.Note.ShouldBe("Fact rationale.");
    revenue.Source!.Note.ShouldBe("Source context.");
    revenue.Cells[0].Note.ShouldBe("Fact rationale.");
    revenue.Cells[1].Note.ShouldBe("Fact cell rationale.\n\nFact rationale.");
    revenue.Cells[2].Note.ShouldBeNull();
    payload.Columns[2].Note.ShouldBe("Calculated column rationale.");

    RowPayload doubled = payload.Rows.Single(row => row.Name.EndsWith(".doubled"));
    doubled.Note.ShouldBe("Formula rationale.");
    doubled.Cells[0].Note.ShouldBe("Formula rationale.");
    doubled.Cells[1].Note.ShouldBe("Formula cell rationale.\n\nFormula rationale.");

    RowPayload spread = payload.Rows.Single(row => row.Name.EndsWith(".spread"));
    spread.Cells[2].Note.ShouldBe("Override rationale.");
  }

  [Fact]
  public void PayloadIdentifiesRowsPresentedFromAnotherView()
  {
    // Arrange
    using var fixture = new BookFixture();
    View view = fixture.LoadView();

    // Act
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    RowPayload linked = payload.Rows.Single(row => row.Name == "model_assumptions.revenue_growth");
    linked.SourceView.ShouldBe("model-assumptions");
    payload.Rows.Single(row => row.Name.EndsWith(".revenue")).SourceView.ShouldBeNull();
    RowPayload linkedFormula = payload.Rows.Single(row => row.Name.EndsWith(".linked_growth"));
    linkedFormula.Formula!.References.Single().SourceView.ShouldBe("model-assumptions");
    linkedFormula.Cells[0].Calculation!.Tokens.Single().SourceView.ShouldBe("model-assumptions");
  }

  [Fact]
  public void PayloadListsTheCellsThatReadEachCellAcrossViews()
  {
    // Arrange
    using var fixture = new BookFixture();
    View view = fixture.LoadView();

    // Act
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    CellPayload revenue = payload.Rows.Single(row => row.Name == "analysis.revenue").Cells[0];
    List<DependentPayload> dependents = revenue.Dependents;
    dependents.ShouldContain(dependent => dependent.Line == "analysis.doubled" && dependent.Column == "2023" &&
      dependent.Value == "200" && dependent.SourceView == null && !dependent.Hidden);
    dependents.ShouldContain(dependent => dependent.Line == "analysis.spread" && dependent.Column == "2025");
    dependents.ShouldContain(dependent => dependent.Line == "analysis.revenue" &&
      dependent.Column == "analysis.cagr" && dependent.ColumnLabel == "CAGR" && dependent.Kind == "formula");
    dependents.ShouldContain(dependent => dependent.Line == "analysis.summary.tripled" &&
      dependent.SourceView == "analysis/summary");
    dependents.ShouldContain(dependent => dependent.Line == "analysis.positive_revenue" && dependent.Hidden);
    payload.Rows.Single(row => row.Name == "analysis.taxed").Cells[2].Dependents.ShouldBeEmpty();
  }

  [Fact]
  public void PayloadMarksCellsThatOnlyReferenceAnotherViewAsLinked()
  {
    // Arrange
    using var fixture = new BookFixture();
    View view = fixture.LoadView();

    // Act
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    RowPayload alias = payload.Rows.Single(row => row.Name.EndsWith(".linked_growth"));
    alias.Cells[0].SourceView.ShouldBe("model-assumptions");
    RowPayload scopedSelfAlias = payload.Rows.Single(row => row.Name.EndsWith(".stub_base"));
    scopedSelfAlias.Cells[3].SourceView.ShouldBeNull();
    scopedSelfAlias.Cells[3].Linked.ShouldBeTrue();
    payload.Rows.Single(row => row.Name.EndsWith(".doubled")).Cells[0].SourceView.ShouldBeNull();
    payload.Rows.Single(row => row.Name.EndsWith(".taxed")).Cells[0].SourceView.ShouldBeNull();
    payload.Rows.Single(row => row.Name.EndsWith(".revenue") && !row.Name.StartsWith("model_assumptions"))
      .Cells[0].SourceView.ShouldBeNull();
  }

  [Fact]
  public void PayloadMarksCrossScopeAliasesAsLinkedWithoutASourceView()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.Write("book.yaml", "name: Bridge book\nshort_name: BRIDGE\nnavigation: [inputs]\n");
    fixture.Write("inputs/interim/facts/figures.csv", "line_item,H1 2025\nrevenue,323369\n");
    fixture.Write("inputs/interim/facts/figures.yaml", """
      table:
        title: Interim figures
      defaults:
        units: millions
      line_items:
        revenue:
          label: Revenue
      """);
    fixture.Write("inputs/formulas.yaml", """
      formulas:
        revenue:
          formula: 0
      column_formulas:
        reported_H1_2025:
          formula: inputs.interim.self["H1 2025"]
      """);
    fixture.Write("inputs/view.yaml", """
      title: Inputs
      columns: [reported_H1_2025]
      rows:
        - line: revenue
      """);

    // Act
    CellPayload alias = fixture.Payload("inputs").Rows.Single().Cells.Single();

    // Assert
    alias.Linked.ShouldBeTrue();
    alias.SourceView.ShouldBeNull();
  }

  [Fact]
  public void PayloadCarriesTheSourceBehindALinkedFact()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.Write("book.yaml", "name: Link book\nshort_name: LINK\nnavigation: [inputs, spread]\n");
    fixture.Write("inputs/facts/figures.csv", "line_item,2025\nrevenue,323369\n");
    fixture.Write("inputs/facts/figures.yaml", """
      table:
        title: Reported figures
      source:
        url: https://example.com/filing
        image: figures.png
      defaults:
        units: millions
      line_items:
        revenue:
          label: Revenue
      """);
    File.WriteAllBytes(Path.Combine(fixture.Root, "inputs", "facts", "figures.png"), new byte[] { 1, 2, 3 });
    fixture.Write("inputs/view.yaml", "title: Inputs\ncolumns: [\"2025\"]\nrows:\n  - line: revenue\n");
    fixture.Write("spread/formulas.yaml", """
      formulas:
        revenue:
          formula: inputs.revenue["2025"]
      """);
    fixture.Write("spread/view.yaml", "title: Spread\ncolumns: [\"2025\"]\nrows:\n  - line: revenue\n");

    // Act
    CellPayload alias = fixture.Payload("spread").Rows.Single().Cells.Single();

    // Assert
    alias.Linked.ShouldBeTrue();
    alias.Source!.Table.ShouldBe("Reported figures");
    alias.Source.Url.ShouldBe("https://example.com/filing");
  }

  [Fact]
  public void PayloadTreatsLinesInheritedFromAnEnclosingScopeAsLocal()
  {
    // Arrange
    using var fixture = new BookFixture();
    Book book = Book.Load(fixture.Root);
    View view = View.Load(book, fixture.PresentationFolder);

    // Act
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    payload.Rows.Single(row => row.Name.EndsWith(".revenue") && !row.Name.StartsWith("model_assumptions"))
      .SourceView.ShouldBeNull();
    payload.Rows.Single(row => row.Name.EndsWith(".doubled")).SourceView.ShouldBeNull();
    payload.Rows.Single(row => row.Name == "model_assumptions.revenue_growth").SourceView.ShouldBe("model-assumptions");
  }

  [Fact]
  public void PayloadLinksFormulaInputsDrawnFromAnEnclosingView()
  {
    // Arrange
    using var fixture = new BookFixture();
    Book book = Book.Load(fixture.Root);
    View view = View.Load(book, fixture.PresentationFolder);

    // Act
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    RowPayload tripled = payload.Rows.Single(row => row.Name.EndsWith(".tripled"));
    tripled.SourceView.ShouldBeNull();
    tripled.Formula!.References.Single().SourceView.ShouldBe("analysis");
    tripled.Cells[0].Calculation!.Tokens[0].SourceView.ShouldBe("analysis");
    RowPayload doubled = payload.Rows.Single(row => row.Name.EndsWith(".doubled"));
    doubled.Formula!.References.Single().SourceView.ShouldBeNull();
    doubled.Formula!.References.Single().Label.ShouldBe("Revenue");
    doubled.Cells[0].Calculation!.Tokens[0].SourceView.ShouldBeNull();
  }

  [Fact]
  public void PayloadLinksSharedAncestorFactsToTheViewPresentingTheirCoordinate()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.Write("book.yaml", """
      name: Shared inputs
      short_name: SHARED
      navigation:
        - spread
        - inputs/financials
        - inputs/capitalization
      """);
    fixture.Write("inputs/facts/financials.csv", "line_item,FY2025\nrevenue,100\n");
    fixture.Write("inputs/facts/financials.yaml", """
      table:
        title: Financials
      line_items:
        revenue:
          label: Revenue
      """);
    fixture.Write("inputs/facts/capitalization.csv", "line_item,Sep. 15 2026\nclosing_share_price,248.42\n");
    fixture.Write("inputs/facts/capitalization.yaml", """
      table:
        title: Capitalization
      line_items:
        closing_share_price:
          label: Closing share price
      """);
    fixture.Write("inputs/financials/view.yaml", """
      title: Financials
      columns: [FY2025]
      rows:
        - line: revenue
      """);
    fixture.Write("inputs/capitalization/view.yaml", """
      title: Capitalization
      columns: [Sep. 15 2026]
      rows:
        - line: closing_share_price
      """);
    fixture.Write("spread/formulas.yaml", """
      formulas:
        revenue:
          formula: inputs.revenue["FY2025"]
        closing_share_price:
          formula: inputs.closing_share_price["Sep. 15 2026"]
      """);
    fixture.Write("spread/view.yaml", """
      title: Spread
      columns: [Spread]
      rows:
        - line: revenue
        - line: closing_share_price
      """);

    // Act
    ViewPayload payload = fixture.Payload("spread");

    // Assert
    RowPayload revenue = payload.Rows.Single(row => row.Name == "spread.revenue");
    revenue.Formula!.References.Single().SourceView.ShouldBe("inputs/financials");
    RowPayload closingSharePrice = payload.Rows.Single(row => row.Name == "spread.closing_share_price");
    FormulaReferencePayload reference = closingSharePrice.Formula!.References.Single();
    reference.SourceView.ShouldBe("inputs/capitalization");
    reference.Linked.ShouldBeTrue();
    closingSharePrice.Cells.Single().Calculation!.Tokens.Single().SourceView.ShouldBe("inputs/capitalization");
  }

  [Fact]
  public void PayloadUsesNavigationOrderWhenSeveralViewsPresentTheSameCoordinate()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.Write("book.yaml", """
      name: Shared inputs
      short_name: SHARED
      navigation: [spread, inputs/primary, inputs/secondary]
      """);
    fixture.Write("inputs/facts/figures.csv", "line_item,2026\nrevenue,100\n");
    fixture.Write("inputs/facts/figures.yaml", "table:\n  title: Figures\n");
    foreach (string id in new[] { "primary", "secondary" })
    {
      fixture.Write($"inputs/{id}/view.yaml", """
        title: Inputs
        columns: [2026]
        rows:
          - line: revenue
        """);
    }
    fixture.Write("spread/formulas.yaml", "formulas:\n  revenue:\n    formula: inputs.revenue[\"2026\"]\n");
    fixture.Write("spread/view.yaml", "title: Spread\ncolumns: [Spread]\nrows:\n  - line: revenue\n");

    // Act
    FormulaReferencePayload reference = fixture.Payload("spread").Rows.Single().Formula!.References.Single();

    // Assert
    reference.SourceView.ShouldBe("inputs/primary");
  }

  [Fact]
  public void PayloadLinksToAViewThatActuallyPresentsTheReferencedCoordinate()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.Write("book.yaml", "name: Shared inputs\nshort_name: SHARED\nnavigation: [spread, inputs, inputs/detail]\n");
    fixture.Write("inputs/facts/figures.csv", "line_item,2026\nrevenue,100\nexpense,40\n");
    fixture.Write("inputs/facts/figures.yaml", "table:\n  title: Figures\n");
    fixture.Write("inputs/view.yaml", "title: Inputs\ncolumns: [2026]\nrows:\n  - line: expense\n");
    fixture.Write("inputs/detail/view.yaml", "title: Detail\ncolumns: [2026]\nrows:\n  - line: revenue\n");
    fixture.Write("spread/formulas.yaml", "formulas:\n  result:\n    formula: inputs.revenue[\"2026\"]\n");
    fixture.Write("spread/view.yaml", "title: Spread\ncolumns: [Spread]\nrows:\n  - line: result\n");

    // Act
    RowPayload result = fixture.Payload("spread").Rows.Single();
    FormulaReferencePayload reference = result.Formula!.References.Single();

    // Assert
    reference.SourceView.ShouldBe("inputs/detail");
    result.Cells.Single().SourceView.ShouldBe("inputs/detail");
    result.Cells.Single().Calculation!.Tokens.Single().SourceView.ShouldBe("inputs/detail");
    fixture.Payload(reference.SourceView!).Rows.Single().Cells.Single().Line.ShouldBe(reference.Line);
  }

  [Fact]
  public void PayloadKeepsCrossScopeReferencesLinkedWithoutAPresentingView()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.Write("book.yaml", "name: Hidden inputs\nshort_name: HIDDEN\nnavigation: [spread]\n");
    fixture.Write("inputs/facts/figures.csv", "line_item,2026\nrevenue,100\n");
    fixture.Write("inputs/facts/figures.yaml", "table:\n  title: Figures\n");
    fixture.Write("spread/formulas.yaml", "formulas:\n  revenue:\n    formula: inputs.revenue[\"2026\"]\n");
    fixture.Write("spread/view.yaml", "title: Spread\ncolumns: [Spread]\nrows:\n  - line: revenue\n");

    // Act
    FormulaReferencePayload reference = fixture.Payload("spread").Rows.Single().Formula!.References.Single();

    // Assert
    reference.SourceView.ShouldBeNull();
    reference.Linked.ShouldBeTrue();
  }

  [Fact]
  public void PayloadPreservesOverrideReferencesAndExpandsHiddenFormulaInputs()
  {
    // Arrange
    using var fixture = new BookFixture();
    View view = fixture.LoadView();

    // Act
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    RowPayload overridden = payload.Rows.Single(row => row.Name.EndsWith(".override_link"));
    CalculationTokenPayload overrideReference = overridden.Cells[0].Calculation!.Tokens.Single();
    overrideReference.Dependency.ShouldBe("model_assumptions.revenue_growth");
    overrideReference.SourceView.ShouldBe("model-assumptions");
    RowPayload taxed = payload.Rows.Single(row => row.Name.EndsWith(".taxed"));
    CalculationTokenPayload intermediate = taxed.Cells[0].Calculation!.Tokens.First(token => token.Dependency is not null);
    intermediate.Dependency.ShouldEndWith(".positive_revenue");
    intermediate.ExpansionId.ShouldNotBeNull();
    payload.Calculations[intermediate.ExpansionId].Tokens.Single(token => token.Dependency is not null)
      .Dependency.ShouldEndWith(".revenue");
    FormulaReferencePayload reference = taxed.Cells[0].Formula!.References
      .First(candidate => candidate.Line.EndsWith(".positive_revenue"));
    payload.Formulas[reference.FormulaId!].Text.ShouldBe("max(revenue, 0)");
  }

  [Fact]
  public void RejectsAnOverrideOfASourcedFact()
  {
    // Arrange
    using var fixture = new BookFixture(true);

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain("cannot replace sourced fact");
  }

  [Fact]
  public void EvaluatesRangesFunctionsAndComparisons()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    View view = fixture.LoadView();
    var engine = new Engine(view);

    // Act
    double? rangeTotal = engine.Cell("range_total", "2023").Value;
    double? rangeAverage = engine.Cell("range_average", "2023").Value;
    double? evenMedian = engine.Cell("even_median", "2023").Value;
    double? oddMedian = engine.Cell("odd_median", "2023").Value;
    double? bounded = engine.Cell("bounded", "2023").Value;
    double? comparison = engine.Cell("comparison", "2023").Value;

    // Assert
    rangeTotal.ShouldBe(221);
    rangeAverage.ShouldBe(110.5);
    evenMedian.ShouldBe(110.5);
    oddMedian.ShouldBe(100);
    bounded.ShouldBe(10);
    comparison.ShouldBe(1);
  }

  [Fact]
  public void EvaluatesOnlyTheSelectedConditionalBranch()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    View view = fixture.LoadView();
    var engine = new Engine(view);

    // Act
    double? trueBranch = engine.Cell("conditional_true", "2023").Value;
    double? falseBranch = engine.Cell("conditional_false", "2023").Value;

    // Assert
    trueBranch.ShouldBe(7);
    falseBranch.ShouldBe(8);
  }

  [Fact]
  public void PayloadEvaluatesOnlyTheSelectedConditionalBranch()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    View view = fixture.LoadView();

    // Act
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    RowPayload conditional = payload.Rows.Single(row => row.Name.EndsWith(".conditional_cycle"));
    conditional.Cells[0].Exact.ShouldBe("7");
    conditional.Cells[0].Calculation.ShouldNotBeNull();
    CalculationPayload calculation = conditional.Cells[0].Calculation!;
    calculation.Tokens.Any(token => token.Dependency != null && token.Dependency.EndsWith(".broken"))
      .ShouldBeFalse();
    calculation.Tokens.ShouldContain(token => token.Text == "not evaluated");
    RowPayload invalidRange = payload.Rows.Single(row => row.Name.EndsWith(".conditional_invalid_range"));
    invalidRange.Cells[0].Exact.ShouldBe("7");
    invalidRange.Cells[0].Calculation.ShouldNotBeNull();
  }

  [Fact]
  public void PayloadReportsAnInvalidDependencyWhenItsConditionalBranchIsSelected()
  {
    // Arrange
    using ConditionalPayloadFixture fixture = new(true);
    View view = fixture.LoadView();

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(
      () => PayloadBuilder.Build(view, new Engine(view), 1));

    // Assert
    exception.Message.ShouldContain("unknown column");
  }

  [Fact]
  public void ChecksFailWhenAnExpressionIsUnresolvedAndSkipOnlyUnavailablePriorPeriods()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    View view = fixture.LoadView();
    var engine = new Engine(view);

    // Act
    List<CheckResult> results = engine.RunChecks();

    // Assert
    CheckResult unresolved = results.Single(result => result.Name == "missing input" && result.Period == "2023");
    unresolved.Passed.ShouldBeFalse();
    unresolved.Delta.ShouldBeNull();
    results.ShouldNotContain(result => result.Name == "change" && result.Period == "2023");
    results.Single(result => result.Name == "change" && result.Period == "2025").Passed.ShouldBeTrue();
    results.Single(result => result.Name == "invalid change" && result.Period == "2023").Passed.ShouldBeFalse();
  }

  [Fact]
  public void CheckPayloadsKeepChecksWithTheSameNameIndependent()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    View view = fixture.LoadView();
    var checks = new List<Check>
    {
      new Check
      {
        Name = "duplicate",
        Scope = view.Scope,
        Expression = ExpressionParser.Parse("1"),
        Text = "1",
        Expect = 1
      },
      new Check
      {
        Name = "duplicate",
        Scope = view.Scope,
        Expression = ExpressionParser.Parse("2"),
        Text = "2",
        Expect = 1
      }
    };

    // Act
    List<CheckPayload> payloads = PayloadBuilder.BuildChecks(view, new Engine(view), checks);

    // Assert
    payloads[0].Passed.ShouldBeTrue();
    payloads[0].Periods.ShouldBe(new[] { "2023", "2025" });
    payloads[1].Passed.ShouldBeFalse();
    payloads[1].Periods.ShouldBe(new[] { "2023", "2025" });
  }

  [Fact]
  public void ChecksWithoutAViewUsePeriodsFromDescendantViews()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    string checkFolder = Path.Combine(fixture.Root, "orphan");
    string viewFolder = Path.Combine(checkFolder, "detail");
    Directory.CreateDirectory(viewFolder);
    File.WriteAllText(Path.Combine(checkFolder, "checks.yaml"), """
      checks:
        constant:
          formula: 1
          expect: 1
        change:
          formula: 1 - prior(1)
          expect: 0
      """);
    File.WriteAllText(Path.Combine(viewFolder, "view.yaml"), """
      title: Detail
      columns: [2024, 2025]
      rows: []
      """);
    Book book = Book.Load(fixture.Root);
    View view = View.Load(book, fixture.ViewFolder);
    List<Check> checks = book.Checks.Where(candidate => candidate.Scope == "orphan").ToList();

    // Act
    List<CheckResult> results = new Engine(view).RunChecks(checks);

    // Assert
    results.Where(result => result.Name == "constant").Select(result => result.Period)
      .ShouldBe(new[] { "2024", "2025" });
    results.Where(result => result.Name == "change").Select(result => result.Period)
      .ShouldBe(new[] { "2025" });
    results.ShouldAllBe(result => result.Passed);
  }

  [Fact]
  public void ChecksSkipPeriodsWhereNoneOfTheirFactsAreEntered()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    string folder = Path.Combine(fixture.Root, "tie");
    string facts = Path.Combine(folder, "facts");
    Directory.CreateDirectory(facts);
    File.WriteAllText(Path.Combine(facts, "balances.csv"), """
      line_item,2023,2024,2025
      current,40,,
      non_current,60,,
      total,100,,
      late,,,5
      offscreen,,,
      """);
    File.WriteAllText(Path.Combine(facts, "balances.yaml"), """
      table:
        title: Balances
      line_items:
        current:
          label: Current
        non_current:
          label: Non-current
        total:
          label: Total
        late:
          label: Late
        offscreen:
          label: Offscreen
      """);
    File.WriteAllText(Path.Combine(folder, "formulas.yaml"), """
      formulas:
        components:
          formula: current + non_current
      """);
    File.WriteAllText(Path.Combine(folder, "checks.yaml"), """
      checks:
        total:
          formula: components - total
        partial:
          formula: total - late
        never:
          formula: offscreen
      """);
    File.WriteAllText(Path.Combine(folder, "view.yaml"), """
      title: Tie
      columns: [2023, 2025]
      rows:
        - line: total
      """);
    Book book = Book.Load(fixture.Root);
    View view = View.Load(book, folder);

    // Act
    List<CheckPayload> payloads = PayloadBuilder.BuildChecks(view, new Engine(view));

    // Assert
    CheckPayload total = payloads.Single(payload => payload.Name == "total");
    total.Periods.ShouldBe(new[] { "2023" });
    total.Passed.ShouldBeTrue();
    CheckPayload partial = payloads.Single(payload => payload.Name == "partial");
    partial.Periods.ShouldBe(new[] { "2023", "2025" });
    partial.Passed.ShouldBeFalse();
    CheckPayload never = payloads.Single(payload => payload.Name == "never");
    never.Periods.ShouldBeEmpty();
    never.Passed.ShouldBeFalse();
  }

  [Fact]
  public void CheckNotesReachTheirPayload()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    File.WriteAllText(Path.Combine(fixture.ViewFolder, "checks.yaml"), """
      checks:
        rounded:
          formula: revenue - 100
          tolerance: 25
          note: Disclosed only to the nearest 50.
      """);
    View view = fixture.LoadView();

    // Act
    List<CheckPayload> payloads = PayloadBuilder.BuildChecks(view, new Engine(view));

    // Assert
    payloads.Single().Note.ShouldBe("Disclosed only to the nearest 50.");
  }

  [Fact]
  public void MissingZeroFactsResolveEmptyPeriodsToZeroButNotCalculatedColumns()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    string folder = Path.Combine(fixture.Root, "charges");
    string facts = Path.Combine(folder, "facts");
    Directory.CreateDirectory(facts);
    File.WriteAllText(Path.Combine(facts, "statement.csv"), """
      line_item,FY,H1 prior,H1 current
      revenue,100,40,60
      """);
    File.WriteAllText(Path.Combine(facts, "statement.yaml"), """
      table:
        title: Statement
      line_items:
        revenue:
          label: Revenue
      """);
    File.WriteAllText(Path.Combine(facts, "charges.csv"), """
      line_item,FY
      fine,7
      """);
    File.WriteAllText(Path.Combine(facts, "charges.yaml"), """
      table:
        title: Charges
      line_items:
        fine:
          label: Fine
          missing: zero
          note: Recognized only in the second half of the fiscal year.
      """);
    File.WriteAllText(Path.Combine(folder, "formulas.yaml"), """
      column_formulas:
        LTM:
          formula: self["FY"] - self["H1 prior"] + self["H1 current"]
          applies_to: facts
      """);
    File.WriteAllText(Path.Combine(folder, "view.yaml"), """
      title: Charges
      columns: [FY, H1 prior, H1 current, LTM]
      rows:
        - line: revenue
        - line: fine
      """);
    Book book = Book.Load(fixture.Root);
    View view = View.Load(book, folder);
    var engine = new Engine(view);

    // Act
    ResolvedCell stub = engine.Cell("fine", "H1 prior");
    ResolvedCell bridged = engine.Cell("fine", "LTM");

    // Assert
    stub.Value.ShouldBe(0);
    stub.Kind.ShouldBe("fact");
    stub.FactCell.ShouldBeNull();
    bridged.Value.ShouldBe(7);
    bridged.Kind.ShouldBe("formula");
  }

  [Fact]
  public void SourcedFactsWinOverAColumnFormulaNamedLikeTheirHeader()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    string folder = fixture.ViewFolder;
    string facts = Path.Combine(folder, "facts");
    File.WriteAllText(Path.Combine(facts, "annual.csv"), "line_item,Annual\nfee,120\n");
    File.WriteAllText(Path.Combine(facts, "annual.yaml"), "table:\n  title: Annual\nline_items:\n  fee: {}\n");
    File.WriteAllText(Path.Combine(facts, "monthly.csv"), "line_item,Monthly\nsubscription,10\n");
    File.WriteAllText(Path.Combine(facts, "monthly.yaml"), "table:\n  title: Monthly\nline_items:\n  subscription: {}\n");
    File.WriteAllText(Path.Combine(folder, "formulas.yaml"), """
      column_formulas:
        Annual:
          formula: self["Monthly"] * 12
          applies_to: facts
        Monthly:
          formula: self["Annual"] / 12
          applies_to: facts
      """);
    File.WriteAllText(Path.Combine(folder, "view.yaml"), """
      title: Recurring
      columns: [Monthly, Annual]
      rows:
        - line: fee
        - line: subscription
      """);
    View view = fixture.LoadView();
    var engine = new Engine(view);

    // Act
    ResolvedCell feeAnnual = engine.Cell("fee", "Annual");
    ResolvedCell feeMonthly = engine.Cell("fee", "Monthly");
    ResolvedCell subscriptionAnnual = engine.Cell("subscription", "Annual");

    // Assert
    feeAnnual.Value.ShouldBe(120);
    feeAnnual.Kind.ShouldBe("fact");
    feeMonthly.Value.ShouldBe(10);
    feeMonthly.Kind.ShouldBe("formula");
    subscriptionAnnual.Value.ShouldBe(120);
  }

  [Fact]
  public void MissingZeroCellsDisplayAsTheDash()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    string folder = fixture.ViewFolder;
    File.WriteAllText(Path.Combine(folder, "facts", "charges.csv"), "line_item,2023,2024\nfine,,7\n");
    File.WriteAllText(Path.Combine(folder, "facts", "charges.yaml"), """
      table:
        title: Charges
      line_items:
        fine:
          label: Fine
          missing: zero
      """);
    File.WriteAllText(Path.Combine(folder, "view.yaml"), "title: Charges\ncolumns: [2023, 2024]\nrows:\n  - line: fine\n");
    View view = fixture.LoadView();

    // Act
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    RowPayload fine = payload.Rows.Single(row => row.Name.EndsWith(".fine"));
    fine.Cells[0].Display.ShouldBe("-");
    fine.Cells[0].Exact.ShouldBe("0");
    fine.Cells[1].Display.ShouldBe("7");
  }

  [Fact]
  public void TableDefaultsSetMissingForEveryLineItem()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    string folder = fixture.ViewFolder;
    File.WriteAllText(Path.Combine(folder, "facts", "charges.csv"), "line_item,2023,2024\nfine,,7\nfee,3,\n");
    File.WriteAllText(Path.Combine(folder, "facts", "charges.yaml"), """
      table:
        title: Charges
      defaults:
        missing: zero
      line_items:
        fine:
          label: Fine
        fee:
          label: Fee
          missing: blank
      """);
    View view = fixture.LoadView();
    var engine = new Engine(view);

    // Act
    ResolvedCell fine = engine.Cell("fine", "2023");
    ResolvedCell fee = engine.Cell("fee", "2024");

    // Assert
    fine.Value.ShouldBe(0);
    fee.Value.ShouldBeNull();
  }

  [Fact]
  public void MissingSettingRejectsUnknownValues()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    string facts = Path.Combine(fixture.ViewFolder, "facts");
    File.WriteAllText(Path.Combine(facts, "charges.csv"), "line_item,2023\nfine,7\n");
    File.WriteAllText(Path.Combine(facts, "charges.yaml"), """
      table:
        title: Charges
      line_items:
        fine:
          label: Fine
          missing: nil
      """);

    // Act
    Action load = () => Book.Load(fixture.Root);

    // Assert
    load.ShouldThrow<InvalidDataException>().Message.ShouldContain("line_items.fine.missing");
  }

  [Fact]
  public void FactOnlyColumnFormulasLetCalculatedLinesEvaluateTheirOwnFormula()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    string folder = Path.Combine(fixture.Root, "bridge");
    string facts = Path.Combine(folder, "facts");
    Directory.CreateDirectory(facts);
    File.WriteAllText(Path.Combine(facts, "statement.csv"), """
      line_item,FY,H1 prior,H1 current
      revenue,100,40,60
      profit,10,2,12
      """);
    File.WriteAllText(Path.Combine(facts, "statement.yaml"), """
      table:
        title: Statement
      line_items:
        revenue:
          label: Revenue
        profit:
          label: Profit
      """);
    File.WriteAllText(Path.Combine(folder, "formulas.yaml"), """
      column_formulas:
        LTM:
          formula: self["FY"] - self["H1 prior"] + self["H1 current"]
          applies_to: facts
        change:
          formula: self["H1 current"] - self["H1 prior"]

      formulas:
        margin:
          formula: profit / revenue
          units: percent
      """);
    File.WriteAllText(Path.Combine(folder, "view.yaml"), """
      title: Bridge
      columns: [FY, H1 prior, H1 current, LTM, change]
      rows:
        - line: revenue
        - line: margin
      """);
    Book book = Book.Load(fixture.Root);
    View view = View.Load(book, folder);
    var engine = new Engine(view);

    // Act
    ResolvedCell revenue = engine.Cell("revenue", "LTM");
    ResolvedCell margin = engine.Cell("margin", "LTM");
    ResolvedCell marginChange = engine.Cell("margin", "change");

    // Assert
    revenue.Value.ShouldBe(120);
    margin.Value!.Value.ShouldBe(20.0 / 120, 1e-12);
    marginChange.Value!.Value.ShouldBe(0.2 - 0.05, 1e-12);
  }

  [Fact]
  public void AnotherFolderReadingALineTakesTheColumnFormulaWhereTheLineLives()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    string results = Path.Combine(fixture.Root, "results");
    string facts = Path.Combine(results, "facts");
    Directory.CreateDirectory(facts);
    File.WriteAllText(Path.Combine(facts, "statement.csv"), """
      line_item,FY,H1 prior,H1 current
      revenue,100,40,60
      """);
    File.WriteAllText(Path.Combine(facts, "statement.yaml"), """
      table:
        title: Statement
      line_items:
        revenue:
          label: Revenue
      """);
    File.WriteAllText(Path.Combine(results, "formulas.yaml"), """
      column_formulas:
        LTM:
          formula: self["FY"] - self["H1 prior"] + self["H1 current"]
          applies_to: facts
      """);
    File.WriteAllText(Path.Combine(results, "view.yaml"), """
      title: Results
      columns: [FY, H1 prior, H1 current, LTM]
      rows:
        - line: revenue
      """);
    string backlog = Path.Combine(fixture.Root, "backlog");
    Directory.CreateDirectory(backlog);
    File.WriteAllText(Path.Combine(backlog, "formulas.yaml"), """
      formulas:
        earned:
          formula: results.revenue
      """);
    File.WriteAllText(Path.Combine(backlog, "view.yaml"), """
      title: Backlog
      columns: [FY, LTM]
      rows:
        - line: earned
      """);
    Book book = Book.Load(fixture.Root);
    var engine = new Engine(View.Load(book, backlog));

    // Act
    ResolvedCell year = engine.Cell("earned", "FY");
    ResolvedCell trailing = engine.Cell("earned", "LTM");

    // Assert
    year.Value.ShouldBe(100);
    trailing.Value.ShouldBe(120);
  }

  [Fact]
  public void RowSignOverridesFormulaPresentation()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    View view = fixture.LoadView();

    // Act
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    RowPayload doubled = payload.Rows.Single(row => row.Name.EndsWith(".doubled"));
    doubled.Cells[0].Contra.ShouldBeTrue();
    doubled.Cells[0].Display.ShouldBe("(200)");
    doubled.Cells[0].Exact.ShouldBe("200");
  }

  [Fact]
  public void ZeroSettingSpellsCalculatedZerosButNotFacts()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    File.WriteAllText(Path.Combine(fixture.Root, "formats.yaml"), """
      defaults:
        units: millions
        zero: "-"
      formats:
        millions:
          decimals: 0
        percent:
          scale: 100
          decimals: 1
          suffix: "%"
      """);
    File.WriteAllText(Path.Combine(fixture.ViewFolder, "facts", "inputs.csv"), """
      line_item,2023,2025
      revenue,0,121
      """);
    View view = fixture.LoadView();

    // Act
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    RowPayload revenue = payload.Rows.Single(row => row.Name.EndsWith(".revenue"));
    RowPayload doubled = payload.Rows.Single(row => row.Name.EndsWith(".doubled"));
    revenue.Cells[0].Display.ShouldBe("0");
    doubled.Cells[0].Display.ShouldBe("-");
    doubled.Cells[0].Exact.ShouldBe("0");
    doubled.Cells[1].Display.ShouldBe("(242)");
  }

  [Fact]
  public void ZeroSettingSpellsHardcodedZeroOverrides()
  {
    // Arrange
    using BookFixture fixture = new BookFixture();
    File.WriteAllText(Path.Combine(fixture.Root, "formats.yaml"), """
      defaults:
        units: millions
        zero: "-"
      formats:
        millions:
          decimals: 0
        percent:
          scale: 100
          decimals: 1
          suffix: "%"
      """);
    string formulasPath = Path.Combine(fixture.ViewFolder, "formulas.yaml");
    File.WriteAllText(formulasPath, File.ReadAllText(formulasPath).Replace("value: 1", "value: 0"));
    View view = fixture.LoadView();

    // Act
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    RowPayload seeded = payload.Rows.Single(row => row.Name.EndsWith(".seeded"));
    seeded.Cells[0].Display.ShouldBe("-");
    seeded.Cells[0].Exact.ShouldBe("0");
  }

  [Fact]
  public void ResolvesScopedSelfAgainstTheCurrentLineInAnotherScope()
  {
    // Arrange
    using var fixture = new BookFixture();
    View view = fixture.LoadView();
    var engine = new Engine(view);

    // Act
    ResolvedCell scopedSelf = engine.Cell("stub_base", "stub");
    ResolvedCell missingCounterpart = engine.Cell("spread", "stub");

    // Assert
    scopedSelf.Value.ShouldBe(150);
    missingCounterpart.Value.ShouldBeNull();
  }


}

internal sealed class BookFixture : TempFolder
{
  public BookFixture(bool overrideFact = false)
  {
    ViewFolder = PathTo("analysis");
    PresentationFolder = PathTo("analysis/summary");

    Write("book.yaml", 
      "name: Test\nshort_name: Test\nunits: millions\nnavigation: []\n");

    Write("formats.yaml", """
      defaults:
        units: millions
        unresolved: ""
        dash: "-"
      formats:
        millions:
          decimals: 0
        percent:
          scale: 100
          decimals: 1
          suffix: "%"
      """);
    Write("analysis/facts/inputs.csv", """
      line_item,2023,2025
      revenue,100,121
      """);
    WriteBytes("analysis/facts/source.png", new byte[] { 1, 2, 3 });
    Write("analysis/facts/inputs.yaml", """
      table:
        title: Inputs
      source:
        image: source.png
        note: Source context.
        details:
          Page: F-6
          Document: test
      defaults:
        units: millions
      line_items:
        revenue:
          label: Revenue
          note: Fact rationale.
          cell_notes:
            2025: Fact cell rationale.
          image_regions:
            2023: { x: 10, y: 20, width: 30, height: 40 }
      """);
    string formulas = """
      formulas:
        doubled:
          formula: revenue * 2
          note: Formula rationale.
          cell_notes:
            2025: Formula cell rationale.
        spread:
          formula: revenue["2025"] - revenue["2023"]
        invalid:
          formula: (-4) ^ 0.5
        range_total:
          formula: sum(revenue["2023":"2025"])
        range_average:
          formula: average(revenue["2023":"2025"])
        even_median:
          formula: median(revenue["2023":"2025"])
        odd_median:
          formula: median(revenue["2023":"2025"], 90)
        bounded:
          formula: min(max(revenue, 0), 10)
        comparison:
          formula: revenue >= 100
        conditional_true:
          formula: if(revenue >= 100, 7, 1 / 0)
        conditional_false:
          formula: if(revenue < 100, 1 / 0, 8)
        broken:
          formula: broken
        conditional_cycle:
          formula: if(1, 7, broken)
        conditional_invalid_range:
          formula: if(1, 7, sum(revenue["missing":"periods"]))
        linked_growth:
          formula: model_assumptions.revenue_growth["2023"]
          units: percent
        positive_revenue:
          formula: max(revenue, 0)
        taxed:
          formula: positive_revenue * model_assumptions.revenue_growth["2023"]
        override_link:
          formula: revenue
        seeded:
          formula: prior(seeded) + 1
        stub_base:
          formula: 100

      column_formulas:
        cagr:
          label: CAGR
          formula: (self["2025"] / self["2023"]) ^ (1 / 2) - 1
          units: percent
          note: Calculated column rationale.
        stub:
          label: Stub
          formula: self["2023"] + summary.self["2025"]
        pointer:
          label: Pointer
          formula: summary.self["2025"]
        latest:
          label: Latest
          formula: self["2025"]

      cell_overrides:
        doubled:
          cagr:
            blank: true
        spread:
          cagr:
            formula: revenue["2025"] - revenue["2023"]
            units: millions
            note: Override rationale.
        override_link:
          2023:
            formula: model_assumptions.revenue_growth["2023"]
            units: percent
        seeded:
          2023:
            value: 1
      """;
    if (overrideFact)
    {
      formulas = formulas.Replace("doubled:\n    cagr:\n      blank: true", "revenue:\n    2023:\n      blank: true");
    }
    Write("analysis/formulas.yaml", formulas);
    Write("analysis/checks.yaml", """
      checks:
        missing input:
          formula: does_not_exist
          expect: 0
        change:
          formula: revenue - prior(revenue)
          expect: 21
        invalid change:
          formula: prior(revenue) + (1 / 0)
          expect: 0
      """);
    Write("model-assumptions/formulas.yaml", """
      formulas:
        revenue_growth:
          formula: 0.1
          units: percent
      """);
    Write("model-assumptions/view.yaml", """
      title: Assumptions
      columns: [2023, 2025]
      rows:
        - line: revenue_growth
      """);
    Write("analysis/summary/formulas.yaml", """
      formulas:
        tripled:
          formula: revenue * 3
        stub_base:
          formula: 50
      """);
    Write("analysis/summary/view.yaml", """
      title: Summary
      columns: [2023, 2025]
      rows:
        - line: revenue
        - line: doubled
        - line: tripled
        - line: model_assumptions.revenue_growth
      """);
    Write("analysis/view.yaml", """
      title: Analysis
      columns: [2023, 2025, cagr, pointer]
      rows:
        - line: revenue
        - line: doubled
          sign: contra
        - line: spread
        - line: invalid
        - line: range_total
        - line: range_average
        - line: even_median
        - line: odd_median
        - line: bounded
        - line: comparison
        - line: conditional_true
        - line: conditional_false
        - line: conditional_cycle
        - line: conditional_invalid_range
        - line: linked_growth
        - line: taxed
        - line: override_link
        - line: seeded
        - line: model_assumptions.revenue_growth
        - line: stub_base
      """);
  }

  public string ViewFolder { get; }
  public string PresentationFolder { get; }

  public View LoadView()
  {
    return View.Load(Book.Load(Root), ViewFolder);
  }


}

internal sealed class ConditionalPayloadFixture : TempFolder
{
  public ConditionalPayloadFixture(bool selectBroken = false)
  {
    ViewFolder = PathTo("v");
    Write("book.yaml", 
      "name: Test\nshort_name: Test\nunits: millions\nnavigation: []\n");
    Write("formats.yaml", """
      defaults:
        unresolved: "—"
      formats:
        millions:
          decimals: 2
      """);
    Write("v/facts/inputs.csv", """
      line_item,2024,2025
      seed,1,2
      """);
    Write("v/facts/inputs.yaml", """
      table:
        title: Inputs
      defaults:
        units: millions
      line_items:
        seed:
          label: Seed
      """);
    Write("v/formulas.yaml", selectBroken
      ? """
        formulas:
          result:
            formula: if(1, broken, 7)
          broken:
            formula: sum(result["missing":"2025"])
        """
      : """
        formulas:
          selected_true:
            formula: if(1, 7, broken)
          selected_false:
            formula: if(0, broken, 8)
          switched:
            formula: if(seed < 2, left, right)
          overridden:
            formula: left
          left:
            formula: seed + 10
          right:
            formula: seed + 20
          broken:
            formula: sum(selected_true["missing":"2025"])
        cell_overrides:
          overridden:
            2025:
              formula: if(1, right, broken)
        """);
    Write("v/view.yaml", selectBroken
      ? """
        title: Review
        columns: [2024, 2025]
        rows:
          - line: result
        """
      : """
        title: Review
        columns: [2024, 2025]
        rows:
          - line: selected_true
          - line: selected_false
          - line: switched
          - line: overridden
        """);
  }

  public string ViewFolder { get; }

  public View LoadView()
  {
    return View.Load(Book.Load(Root), ViewFolder);
  }


}