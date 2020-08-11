﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using TinyBI.Engine.JsonModels;
using Dapper;
using HandlebarsDotNet;

namespace TinyBI
{
    public class Query
    {
        public IList<IColumn> Select { get; }
        public IList<Aggregation> Aggregations { get; }
        public IList<Filter> Filters { get; }
        public IList<Ordering> OrderBy { get; }

        public bool Totals { get; }

        public long Skip { get; set; }

        public int Take { get; set; }

        public Query(QueryJson json, Schema schema)
        {
            Select = schema.Load(json.Select);
            Aggregations = json.Aggregations.Select(x => new Aggregation(x, schema)).ToList();
            Filters = Filter.Load(json.Filters, schema);
            OrderBy = Ordering.Load(json.OrderBy, schema);
            Totals = json.Totals;
            Skip = json.Skip;
            Take = json.Take;
        }

        private static readonly Func<object, string> _template = Handlebars.Compile(@"

with {{#each Aggregations}}

    [Aggregation{{@index}}] as (
        {{{this}}}
    )
    {{#unless @last}},{{/unless}}

{{/each}}

select

{{#each Select}}
    a0.Select{{@index}},
{{/each}}

{{#each Aggregations}}
    a{{@index}}.Value0 Value{{@index}}
    {{#unless @last}},{{/unless}}
{{/each}}

from Aggregation0 a0

{{#each Aggregations}}
    {{#unless @first}}
        {{#if ../Select}}
            left join Aggregation{{@index}} a{{@index}} on
            {{#each ../Select}}
                a{{../../@index}}.Select{{@index}} = a0.Select{{@index}}
                {{#unless @last}}and{{/unless}}
            {{/each}}
        {{/if}}
        {{#unless ../Select}}
            cross join Aggregation{{@index}} a{{@index}}
        {{/unless}}
    {{/unless}}
{{/each}}

order by {{ordering}}

offset {{skip}} rows
fetch next {{take}} rows only
");

        private string ToSql(IList<IColumn> select, IFilterParameters filterParams,
                             IEnumerable<Filter> outerFilters, long skip, int take)
        {
            if (Aggregations.Count == 1 && OrderBy.Count == 0)
            {
                return Aggregations[0].ToSql(select, outerFilters.Concat(Filters), filterParams, skip, take);
            }

            var ordering = "a0.Value0 desc";

            if (select != null && OrderBy.Count != 0)
            {
                ordering = string.Join(", ", OrderBy.Select(FindOrderingColumn));
            }

            return _template(new
            {
                Skip = skip,
                Take = take,
                Aggregations = Aggregations.Select(x =>
                    x.ToSql(select, outerFilters.Concat(Filters), filterParams)),
                Select = select,
                ordering
            });
        }

        private string FindOrderingColumn(Ordering ordering)
        {
            var i = Select.IndexOf(ordering.Column);
            if (i == -1)
            {
                throw new InvalidOperationException(
                    $"Cannot order by {ordering.Column} as it has not been selected");
            }

            var direction = ordering.Descending ? "desc" : "asc";

            return $"a0.Select{i} {direction}";
        }

        public string ToSql(IFilterParameters filterParams,
                            IEnumerable<Filter> outerFilters)
        {
            var sql = ToSql(Select, filterParams, outerFilters, Skip, Take);

            if (Totals)
            {
                sql += ";" + ToSql(null, filterParams, outerFilters, 0, 1);
            }

            return sql;
        }
        
        public QueryResultJson Run(IDbConnection db, Action<string> log, params Filter[] outerFilters)
        {
            var filterParams = new DapperFilterParameters();

            var sql = ToSql(filterParams, outerFilters);

            log?.Invoke($"{sql} with parameters: {filterParams}");

            var reader = db.QueryMultiple(sql, filterParams.DapperParams);

            var result = new QueryResultJson();

            result.Records = ConvertRecords(reader.Read<dynamic>()
                                .Cast<IDictionary<string, object>>());                     
            if (Totals)
            {
                result.Totals = ConvertRecords(reader.Read<dynamic>()
                                .Cast<IDictionary<string, object>>())
                                .Single();
            }

            return result;
        }

        private static IList<QueryRecordJson> ConvertRecords(IEnumerable<IDictionary<string, object>> list)
            => list.Select(x => new QueryRecordJson
                {
                     Selected = GetList(x, "Select"),
                     Aggregated = GetList(x, "Value"),
                })
                .ToList();
        

        private static IList<object> GetList(IDictionary<string, object> raw, string prefix)
        {
            IList<object> result = null;

            for (var n = 0; n < 100; n++)
            {
                if (!raw.TryGetValue($"{prefix}{n}", out var value)) break;
                if (result == null) result = new List<object>();
                result.Add(value);
            }

            return result;
        }
    }
}
