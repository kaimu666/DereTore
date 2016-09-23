﻿using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using DereTore.Applications.StarlightDirector.UI.Controls;

namespace DereTore.Applications.StarlightDirector.UI.Converters {
    public sealed class EditModeToIsCheckedConverter : IValueConverter {

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            EditMode e, p;
            e = (EditMode)value;
            p = (EditMode)parameter;
            return e == p;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            throw new NotSupportedException();
        }

    }
}