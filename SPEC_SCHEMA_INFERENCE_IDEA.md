TL;DR
Below is a production-grade, LLM-ready feature spec for your CLI schema inference tool. It is deterministic, SQL Server–focused, explainable, and directly usable in Claude Code or Codex. You can paste this as-is into an LLM and expect solid implementation output.



📄 SPEC: Excel → SQL Server Schema Inference CLI

1. Overview

Build a .NET CLI tool that:

Reads an Excel file (.xlsx)
Infers a SQL Server table schema from the data
Outputs:
CREATE TABLE SQL
Optional detailed inference report (JSON)
The system must use deterministic, rule-based inference with statistical heuristics, not AI/ML.



2. Supported SQL Types (STRICT SCOPE)

The system MUST only infer the following SQL Server types:

INT
BIGINT
DECIMAL(p,s)
FLOAT
BIT
DATETIME2
NVARCHAR(n)
NVARCHAR(MAX)
No other types are allowed.



3. High-Level Inference Pipeline

For each column:

Extract values
Normalize values
Sample values
Evaluate type candidates
Score candidates
Select best type based on rules
Determine nullability
Determine final SQL type parameters (length, precision, scale)


4. Input Assumptions

First row = column headers
Remaining rows = data
Empty cells are allowed
Mixed-type columns are expected


5. Sampling Strategy

Default:

Sample first 1,000 rows
Adaptive Rule:

If confidence < threshold:
Increase sample size (up to full dataset)
CLI Option:

--sample-size <int> (default: 1000)



6. Normalization Rules

Before type evaluation:

Trim whitespace
Convert empty strings → NULL
Normalize common null tokens:
"null", "NULL", "N/A", "-" → NULL


7. Type Candidates and Evaluation Order

Evaluate in this order:

BIT
INT
BIGINT
DECIMAL
FLOAT
DATETIME2
NVARCHAR (fallback)


8. Type Evaluation Rules

For each candidate type:

8.1 Track Metrics

For sampled values:

total_count
null_count
valid_count (values successfully parsed)
invalid_count
valid_ratio = valid_count / (total_count - null_count)


9. Type-Specific Parsing Rules

9.1 BIT

Valid values:

0, 1
true, false (case insensitive)


9.2 INT

Must fit 32-bit signed integer
No decimals


9.3 BIGINT

Must fit 64-bit signed integer


9.4 DECIMAL

Fixed-point numbers
Track:
max precision
max scale


9.5 FLOAT

Any valid floating-point number
Used when DECIMAL precision exceeds safe limits


9.6 DATETIME2

Attempt parsing using multiple formats
Count successful parses


9.7 NVARCHAR

Fallback if no other type meets threshold



10. Type Selection Rules

10.1 Confidence Threshold

Default:

--confidence-threshold 0.9

10.2 Selection Logic

For each type in order:

If valid_ratio >= threshold
→ candidate is eligible
10.3 First-Match Wins

Select the first eligible type in evaluation order


11. Conflict Handling

Example:

9 integers, 1 invalid string
If:

valid_ratio = 0.9
threshold = 0.9
→ INT is selected

Behavior:

Invalid values become NULL (permissive mode)


12. Modes

12.1 Permissive (default)

Invalid values → NULL
Type chosen based on threshold
12.2 Strict

--mode strict

If any invalid values exist:
→ reject candidate type
May force fallback to NVARCHAR


13. Nullability Rules

if null_count > 0:

    column is NULLABLE

else:

    NOT NULL



14. NVARCHAR Length Inference

Rules:

Compute:
max_length
p95_length (95th percentile)
Default Strategy:

length = min(max_length, 255)

If max_length > 4000:

→ use NVARCHAR(MAX)



15. DECIMAL Precision/Scale

Track per value:

digits before decimal
digits after decimal
Final:

precision = max(total_digits)

scale = max(fraction_digits)

Cap:

precision <= 38

If exceeded:
→ fallback to FLOAT



16. DATETIME Threshold

if valid_ratio >= threshold:

    DATETIME2

else:

    continue



17. Output: SQL Generation

Example:

CREATE TABLE [MyTable] (

    [Id] INT NOT NULL,

    [Amount] DECIMAL(10,2) NULL,

    [CreatedAt] DATETIME2 NULL,

    [Notes] NVARCHAR(255) NULL

);



18. CLI Interface

Command

sqlxl infer-schema <input.xlsx>

Options

--table <name>

--schema <name> (default: dbo)

--sample-size <int>

--confidence-threshold <float>

--mode strict|permissive

--max-varchar <int> (default: 255)

--report <path.json>



19. JSON Report (Optional but REQUIRED Feature)

If --report specified, output:

{

  "columns": [

    {

      "name": "Amount",

      "inferred_type": "DECIMAL(10,2)",

      "valid_ratio": 0.97,

      "null_ratio": 0.05,

      "invalid_count": 3,

      "sample_size": 1000

    }

  ]

}



20. Determinism Requirement

The system MUST:

Produce identical output for identical input
Not depend on randomness unless explicitly seeded


21. Error Handling

Fail if:
File not found
No columns detected
Warn (not fail) on:
Type coercions
High invalid ratios


22. Example Input → Output

Input (Excel)

Id

Amount

Active

CreatedAt

1

10.50

true

2024-01-01

2

20.00

false

2024-01-02

3

abc

true

invalid



Output

CREATE TABLE [MyTable] (

    [Id] INT NOT NULL,

    [Amount] DECIMAL(4,2) NULL,

    [Active] BIT NOT NULL,

    [CreatedAt] NVARCHAR(255) NULL

);



23. Non-Goals (Explicit)

No machine learning
No schema merging across files
No user-defined type plugins (future)
No streaming ingestion (batch only)


24. Implementation Guidance (for LLM)

Use streaming where possible but allow sampling buffer
Separate:
parsing
inference
output generation
Write unit tests per type rule
Keep inference engine pure (no I/O)


Final Note

This spec gives you something most tools don’t:

Predictable, explainable schema inference

That’s your differentiation vs things like pandas—which optimize for convenience, not determinism.