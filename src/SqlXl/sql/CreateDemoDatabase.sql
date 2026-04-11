/*=============================================================================
  SqlXL Demo Database — CreateDemoDatabase.sql

  WARNING: This script DROPS and RECREATES the SqlXlDemo database every time.
  All existing data will be permanently lost.

  Embedded resource — executed by: sqlxl demo --connection "..."
=============================================================================*/

USE master;
GO

-- Drop and recreate SqlXlDemo
IF EXISTS (SELECT * FROM sys.databases WHERE name = N'SqlXlDemo')
BEGIN
    ALTER DATABASE SqlXlDemo SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SqlXlDemo;
END
GO

CREATE DATABASE SqlXlDemo;
GO

USE SqlXlDemo;
GO

/*=============================================================================
  DOMAIN TABLES
=============================================================================*/

-- Departments
CREATE TABLE dbo.Departments (
    DepartmentId    INT IDENTITY(1,1) PRIMARY KEY,
    Name            NVARCHAR(100) NOT NULL,
    Budget          DECIMAL(18, 2) NOT NULL,
    StartDate       DATETIME NOT NULL,
    Revenue         MONEY NOT NULL,
    RecordUpdated   DATETIME2 NOT NULL,
    EmployeeCount   SMALLINT NOT NULL,
    OfficeNumber    TINYINT NOT NULL,
    OperationCost   FLOAT NOT NULL,
    UNIQUE (Name)
);

-- Employees
CREATE TABLE dbo.Employees (
    EmployeeId      INT IDENTITY(1,1) PRIMARY KEY,
    FirstName       NVARCHAR(50) NOT NULL,
    LastName        NCHAR(25) NOT NULL,
    DateOfBirth     DATETIME NOT NULL,
    DepartmentId    INT,
    Email           VARCHAR(100) NOT NULL,
    Salary          MONEY NOT NULL,
    HireDate        DATETIME2 NOT NULL,
    PartTime        BIT NOT NULL,
    YearsInCompany  SMALLINT NOT NULL,
    PreviousSalary  DECIMAL(18, 2) NOT NULL,
    EmployeeCode    CHAR(10) NOT NULL,
    UNIQUE (Email),
    FOREIGN KEY (DepartmentId) REFERENCES dbo.Departments(DepartmentId)
);

-- Projects
CREATE TABLE dbo.Projects (
    ProjectId    INT IDENTITY(1,1) PRIMARY KEY,
    ProjectName  NVARCHAR(100) NOT NULL,
    StartDate    DATETIME NOT NULL,
    EndDate      DATETIME2,
    DepartmentId INT,
    Budget       DECIMAL(18, 4) NOT NULL,
    Cost         MONEY NOT NULL,
    IsActive     BIT NOT NULL,
    TeamSize     TINYINT NOT NULL,
    UNIQUE (ProjectName),
    FOREIGN KEY (DepartmentId) REFERENCES dbo.Departments(DepartmentId)
);

-- Assignments
CREATE TABLE dbo.Assignments (
    AssignmentId    INT IDENTITY(1,1) PRIMARY KEY,
    ProjectId       INT NOT NULL,
    EmployeeId      INT NOT NULL,
    AssignmentDate  DATETIME NOT NULL,
    DurationHours   SMALLINT NOT NULL,
    FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId),
    FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees(EmployeeId)
);

-- Categories
CREATE TABLE dbo.Categories (
    CategoryID   INT IDENTITY(1,1) PRIMARY KEY,
    CategoryName NVARCHAR(100) NOT NULL,
    Description  NVARCHAR(500) NULL,
    UNIQUE (CategoryName)
);

-- Products
CREATE TABLE dbo.Products (
    ProductID     INT IDENTITY(1,1) PRIMARY KEY,
    ProductName   NVARCHAR(100) NOT NULL,
    Price         DECIMAL(10, 2) NOT NULL,
    CategoryID    INT NOT NULL,
    StockQuantity INT NULL,
    IsActive      BIT NOT NULL DEFAULT (1),
    FOREIGN KEY (CategoryID) REFERENCES dbo.Categories(CategoryID),
    CHECK (Price > 0),
    CHECK (StockQuantity >= 0)
);

/*=============================================================================
  CONSTRAINT / DATATYPE TEST TABLES
  (retained for developer testing coverage)
=============================================================================*/

CREATE TABLE dbo.UniqueTestItems (
    ID      INT IDENTITY(1,1) PRIMARY KEY,
    ColumnA VARCHAR(100),
    ColumnB VARCHAR(100),
    ColumnC INT,
    ColumnD INT,
    ColumnE INT,
    CONSTRAINT UQ_ColumnA         UNIQUE (ColumnA),
    CONSTRAINT UQ_ColumnB_ColumnC UNIQUE (ColumnB, ColumnC),
    CONSTRAINT UQ_Columns_C_D_E   UNIQUE (ColumnC, ColumnD, ColumnE)
);

CREATE TABLE dbo.SqlDataTypesTestItems (
    ID                          INT IDENTITY(1,1) PRIMARY KEY,
    String10Required            NVARCHAR(10) NOT NULL,
    String50                    NVARCHAR(50) NULL,
    String255                   NVARCHAR(255) NULL,
    StringMax                   NVARCHAR(MAX) NULL,
    SmallIntegerRequiredColumn  SMALLINT NOT NULL,
    IntegerColumn               INT NULL,
    BigIntegerColumn            BIGINT NULL,
    TinyIntegerColumn           TINYINT NULL,
    DecimalValueColumn          DECIMAL(10,2) NULL,
    MoneyValueRequired          MONEY NOT NULL,
    FloatValueColumn            FLOAT NULL,
    DateOnly                    DATE NULL,
    DateTimeValue               DATETIME NULL,
    DateTime2ValueRequired      DATETIME2 NOT NULL,
    TimeOnlyColumn              TIME NULL,
    BooleanValueColumn          BIT NULL
);
GO

/*=============================================================================
  SAMPLE DATA
=============================================================================*/

INSERT INTO dbo.Departments (Name, Budget, StartDate, Revenue, RecordUpdated, EmployeeCount, OfficeNumber, OperationCost) VALUES
('Human Resources', 500000,  '20230101 08:00:00', 200000, '20230415 14:30:00', 15, 1, 120000),
('Engineering',    2000000, '20230101 08:00:00', 800000, '20230415 14:30:00', 40, 2, 300000),
('Marketing',       750000,  '20230101 08:00:00', 350000, '20230415 14:30:00', 20, 3, 150000);

INSERT INTO dbo.Employees (FirstName, LastName, DateOfBirth, DepartmentId, Email, Salary, HireDate, PartTime, YearsInCompany, PreviousSalary, EmployeeCode) VALUES
('John',  'Doe',     '19900101 09:00:00', 1, 'john.doe@example.com',   75000, '20210101 09:00:00', 0, 10, 70000, 'JD12345678'),
('Jane',  'Smith',   '19890201 09:00:00', 2, 'jane.smith@example.com', 85000, '20210201 09:00:00', 1,  5, 80000, 'JS87654321'),
('Alice', 'Johnson', '19750315 09:00:00', 2, 'alice.johnson@example.com', 65000, '20210301 09:00:00', 0, 3, 60000, 'AJ12348765');

INSERT INTO dbo.Projects (ProjectName, StartDate, EndDate, DepartmentId, Budget, Cost, IsActive, TeamSize) VALUES
('Project A', '20230415 10:00:00', '20240414 18:00:00', 1, 150000.0000, 100000, 1, 5),
('Project B', '20230415 10:00:00', '20240414 18:00:00', 2, 250000.0000, 150000, 1, 10);

INSERT INTO dbo.Assignments (ProjectId, EmployeeId, AssignmentDate, DurationHours) VALUES
(1, 1, '20230415 10:00:00', 8),
(2, 2, '20230415 10:00:00', 6),
(2, 3, '20230415 10:00:00', 4);

INSERT INTO dbo.UniqueTestItems (ColumnA, ColumnB, ColumnC, ColumnD, ColumnE) VALUES
('SampleText', 'ColBSampleText', 2, 3, 4);

INSERT INTO dbo.Categories (CategoryName, Description) VALUES
('Electronics',     'Phones, laptops, and gadgets'),
('Clothing',        'Apparel and accessories'),
('Books',           'Physical and digital books'),
('Home & Garden',   'Furniture, decor, and outdoor items'),
('Sports & Outdoors','Athletic equipment and outdoor gear');

INSERT INTO dbo.Products (ProductName, Price, CategoryID, StockQuantity, IsActive) VALUES
('iPhone 15 Pro',              999.00, 1,  45, 1),
('Samsung Galaxy Laptop',     1199.00, 1,  12, 1),
('Wireless Earbuds',            79.00, 1, 200, 1),
('USB-C Cable 6ft',              9.99, 1, 500, 1),
('Blue Denim Jeans',            49.99, 2,  75, 1),
('Cotton T-Shirt (White)',      19.99, 2, 150, 1),
('Winter Jacket',              129.00, 2,  30, 1),
('Running Shoes',               89.99, 2,   0, 1),
('Clean Code by Robert Martin', 34.99, 3,  25, 1),
('The Pragmatic Programmer',    39.99, 3,  18, 1),
('Design Patterns (Gang of Four)', 44.99, 3, NULL, 1),
('Office Desk Chair',          249.00, 4,   8, 1),
('LED Floor Lamp',              59.99, 4,  22, 1),
('Garden Hose 50ft',            29.99, 4,  35, 0),
('Yoga Mat Premium',            39.99, 4,  65, 1),
('Camping Tent 4-Person',      199.00, 5,  14, 1),
('Basketball Official Size',    34.99, 5,  88, 1);
GO
