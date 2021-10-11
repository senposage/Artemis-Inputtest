﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;

namespace Artemis.UI.Avalonia.Converters
{
    public class ValuesAdditionConverter : IMultiValueConverter
    {
        /// <inheritdoc />
        public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture)
        {
            return values.Where(v => v is double).Cast<double>().Sum();
        }
    }
}